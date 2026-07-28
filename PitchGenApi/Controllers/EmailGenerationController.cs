using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PitchGenApi.Database;
using PitchGenApi.Interfaces;
using PitchGenApi.Model;
using PitchGenApi.Model.DTOs;
using PitchGenApi.Models;
using PitchGenApi.Services;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PitchGenApi.Controllers
{
    [ApiController]
    [Route("api/email-generation")]
    public class EmailGenerationController : ControllerBase
    {
        private readonly AppDbContext _dbContext;
        private readonly IPitchService _pitchService;
        private readonly ContactRepository _contactRepository;
        private readonly INoteRepository _noteRepository;
        private readonly DeepSeekPitchService _deepSeekService;

        public EmailGenerationController(
            AppDbContext dbContext,
            IPitchService pitchService,
            ContactRepository contactRepository,
            INoteRepository noteRepository,
            DeepSeekPitchService deepSeekService)
        {
            _dbContext = dbContext;
            _pitchService = pitchService;
            _contactRepository = contactRepository;
            _noteRepository = noteRepository;
            _deepSeekService = deepSeekService;
        }

        // ============================================
        // 🚀 SINGLE-CONTACT EMAIL GENERATION
        //    Returns the email + web-search data + final prompt
        //    + all resolved inputs that fed the generation.
        // ============================================
        [HttpPost("generate")]
        public async Task<IActionResult> GenerateSingleContactEmail(
            [FromBody] GenerateSingleContactCampaignEmailRequest request)
        {
            try
            {
                if (request == null)
                    return BadRequest(new { Message = "Request body is required" });

                if (request.BlueprintId <= 0)
                    return BadRequest(new { Message = "Valid BlueprintId is required" });

                if (request.ContactId <= 0)
                    return BadRequest(new { Message = "Valid ContactId is required" });

                if (string.IsNullOrWhiteSpace(request.ClientId))
                    return BadRequest(new { Message = "ClientId is required" });

                if (!int.TryParse(request.ClientId, out var parsedClientId))
                    return BadRequest(new { Message = "ClientId must be numeric" });

                var template = await _dbContext.CampaignTemplates
                    .Include(t => t.TemplateDefinition)
                    .FirstOrDefaultAsync(t =>
                        t.Id == request.BlueprintId &&
                        t.ClientId == request.ClientId);

                if (template == null)
                    return NotFound(new { Message = "Campaign template not found" });

                if (template.TemplateDefinition == null)
                    return StatusCode(500, new { Message = "Template definition is missing" });

                var contact = await _dbContext.contacts
                    .Include(c => c.data_file)
                    .FirstOrDefaultAsync(c =>
                        c.id == request.ContactId &&
                        c.data_file.client_id == parsedClientId);

                if (contact == null)
                    return NotFound(new { Message = "Contact not found" });

                if (!request.OverwriteExisting && !string.IsNullOrWhiteSpace(contact.email_body))
                {
                    return Ok(new
                    {
                        Success = true,
                        Message = "Email already exists for this contact",
                        Generated = false,
                        ContactId = contact.id,
                        EmailSubject = contact.email_subject,
                        EmailBody = contact.email_body
                    });
                }

                var campaignPlaceholderValues =
                    string.IsNullOrWhiteSpace(template.PlaceholderValues)
                        ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        : JsonSerializer.Deserialize<Dictionary<string, string>>(template.PlaceholderValues)
                          ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                var customFields = await (
                    from value in _dbContext.contact_custom_field_values
                    join field in _dbContext.crm_custom_fields
                        on value.field_id equals field.id
                    where value.contact_id == request.ContactId
                    select new { field.field_name, value.value }
                ).ToDictionaryAsync(
                    x => x.field_name,
                    x => x.value ?? "",
                    StringComparer.OrdinalIgnoreCase
                );

                var currentDate = DateTime.UtcNow.ToString("MMMM d, yyyy");
                var generationNotes = await GetGenerationNotesAsync(parsedClientId, request.ContactId);

                // ---- runtime replacements ----
                var runtimeReplacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["full_name"] = contact.full_name ?? $"{contact.first_name} {contact.last_name}".Trim(),
                    ["first_name"] = contact.first_name ?? "",
                    ["last_name"] = contact.last_name ?? "",
                    ["company_name"] = contact.company_name ?? "",
                    ["company_name_friendly"] = contact.company_name ?? "",
                    ["job_title"] = contact.job_title ?? "",
                    ["location"] = contact.country_or_address ?? "",
                    ["linkedin_url"] = contact.linkedin_url ?? "",
                    ["website"] = contact.website ?? "",
                    ["linkedin_info"] = StripHtml(contact.linkedIninformation),
                    ["date"] = currentDate,
                    ["notes"] = generationNotes,
                    ["use_email"] = "",
                    ["use_emails"] = "",
                    ["search_output_summary"] = ""
                };

                foreach (var kv in customFields)
                    runtimeReplacements[kv.Key] = kv.Value ?? "";

                // runtime keys must SURVIVE the campaign-level pass
                var campaignOnlyValues = campaignPlaceholderValues
                    .Where(kv => !runtimeReplacements.ContainsKey(kv.Key))
                    .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

                var campaignBlueprint = ApplyPlaceholders(
                    template.TemplateDefinition.MasterBlueprintUnpopulated ?? "",
                    campaignOnlyValues
                );

                // ---- email-conversation context (use_email / use_emails) ----
                var emailHistoryEnabled =
                    campaignPlaceholderValues.TryGetValue("use_email_history", out var histVal) &&
                    (histVal ?? "").Trim().ToLower() == "yes";

                if (emailHistoryEnabled ||
                    ContainsPlaceholder(campaignBlueprint, "use_email") ||
                    ContainsPlaceholder(campaignBlueprint, "use_emails"))
                {
                    var emailContext = await GetEmailConversationContextAsync(parsedClientId, request.ContactId);
                    runtimeReplacements["use_email"] = emailContext;
                    runtimeReplacements["use_emails"] = emailContext;
                }

                // ---- model + GPT detection ----
                var selectedModel = !string.IsNullOrWhiteSpace(template.SelectedModel)
                    ? template.SelectedModel
                    : (!string.IsNullOrWhiteSpace(template.TemplateDefinition.SelectedModel)
                        ? template.TemplateDefinition.SelectedModel
                        : "gpt-5.1");

                var isGptModel = selectedModel.Trim().StartsWith("gpt", StringComparison.OrdinalIgnoreCase);

                // ---- body prompt ----
                var finalPrompt = ApplyPlaceholders(campaignBlueprint, runtimeReplacements);

                // Email history ON but no {use_emails} placeholder → append it
                var pastEmails = runtimeReplacements["use_emails"];
                if (emailHistoryEnabled &&
                    !string.IsNullOrWhiteSpace(pastEmails) &&
                    !ContainsPlaceholder(campaignBlueprint, "use_email") &&
                    !ContainsPlaceholder(campaignBlueprint, "use_emails"))
                {
                    finalPrompt = $"{finalPrompt}\n\nPrevious email conversation with this contact:\n{pastEmails}";
                }

                // ---- web / personalization search (non-GPT only) ----
                PitchResult? searchResult = null;
                string webSearchData = "";
                string filledSearchInstructions = "";

                if (!isGptModel)
                {
                    var personalization = campaignPlaceholderValues.TryGetValue("use_personalization_search", out var ps)
                        ? (ps ?? "").Trim().ToLower()
                        : "";

                    if (personalization == "no")
                    {
                        finalPrompt = finalPrompt.Replace("{web_searched_data}", "");
                    }
                    else
                    {
                        var instructionTemplate = !string.IsNullOrWhiteSpace(template.TemplateDefinition.WebSearchInstructions)
                            ? template.TemplateDefinition.WebSearchInstructions
                            : (campaignPlaceholderValues.TryGetValue("search_objective", out var so) ? so ?? "" : "");

                        var webSearchReplacements =
                            new Dictionary<string, string>(campaignPlaceholderValues, StringComparer.OrdinalIgnoreCase);

                        webSearchReplacements["hook"] =
                            (campaignPlaceholderValues.TryGetValue("hook", out var hk) && !string.IsNullOrWhiteSpace(hk))
                                ? hk
                                : (campaignPlaceholderValues.TryGetValue("hook_search_terms", out var hst) ? hst ?? "" : "");

                        foreach (var kv in runtimeReplacements)
                            webSearchReplacements[kv.Key] = kv.Value;

                        filledSearchInstructions = ApplyPlaceholders(instructionTemplate, webSearchReplacements);

                        if (string.IsNullOrWhiteSpace(filledSearchInstructions))
                        {
                            finalPrompt = finalPrompt.Replace("{web_searched_data}", "");
                        }
                        else
                        {
                            searchResult = await _pitchService.GenerateWebSearchAsync(new EnquiryRequest
                            {
                                Prompt = filledSearchInstructions,
                                ScrappedData = "",
                                ModelName = "gpt-4o-mini-search-preview"
                            }, parsedClientId);

                            if (searchResult != null && searchResult.IsSuccess)
                                webSearchData = searchResult.Content ?? "";

                            finalPrompt = finalPrompt.Contains("{web_searched_data}")
                                ? finalPrompt.Replace("{web_searched_data}", webSearchData)
                                : $"{finalPrompt}\n\n{webSearchData}";
                        }
                    }

                    runtimeReplacements["search_output_summary"] = webSearchData;
                }

                // ---- system prompt is EMPTY (matches frontend) ----
                var systemPrompt = "";

                var bodyResult = await GeneratePitchByProviderAsync(new EnquiryRequest
                {
                    Prompt = finalPrompt,
                    ScrappedData = systemPrompt,
                    ModelName = selectedModel
                });

                if (!bodyResult.IsSuccess || string.IsNullOrWhiteSpace(bodyResult.Content))
                {
                    return StatusCode(500, new
                    {
                        Message = "Failed to generate email body",
                        Error = bodyResult.Content,
                        FinalPrompt = finalPrompt,
                        WebSearchData = webSearchData
                    });
                }

                // ---- subject ----
                string subjectLine = "";
                PitchResult? subjectResult = null;
                string filledSubjectInstruction = "";

                var aiMode = campaignPlaceholderValues.TryGetValue("email_subject-AI", out var aiModeValue)
                    ? (aiModeValue ?? "").Trim().ToLower()
                    : "yes";

                var manualSubjectTemplate = campaignPlaceholderValues.TryGetValue("email_subject-manual", out var manualVal)
                    ? manualVal ?? ""
                    : "";

                var subjectReplacements = new Dictionary<string, string>(runtimeReplacements, StringComparer.OrdinalIgnoreCase)
                {
                    ["generated_pitch"] = bodyResult.Content ?? ""
                };

                var isAiSubject = aiMode != "no";

                if (isAiSubject)
                {
                    filledSubjectInstruction = ApplyPlaceholders(
                        template.SubjectInstructions ?? "",
                        subjectReplacements
                    );

                    subjectResult = await GeneratePitchByProviderAsync(new EnquiryRequest
                    {
                        Prompt = bodyResult.Content,
                        ScrappedData = filledSubjectInstruction,
                        ModelName = selectedModel
                    });

                    if (subjectResult.IsSuccess)
                        subjectLine = subjectResult.Content ?? "";
                }
                else if (!string.IsNullOrWhiteSpace(manualSubjectTemplate))
                {
                    subjectLine = ApplyPlaceholders(manualSubjectTemplate, subjectReplacements);
                }

                // ---- Preview mode: no DB write, no credit, no history ----
                if (!request.Preview)
                {
                    contact.email_body = bodyResult.Content;
                    contact.email_subject = subjectLine;
                    contact.updated_at = DateTime.UtcNow;

                    await _dbContext.SaveChangesAsync();

                    await _contactRepository.CreditDeduction(parsedClientId);
                    await _contactRepository.SaveKraftHistoryAsync(request.ContactId, parsedClientId, null, request.BlueprintId, "Reply");
                }

                return Ok(new
                {
                    Success = true,
                    Generated = true,
                    Preview = request.Preview,
                    BlueprintId = request.BlueprintId,
                    ContactId = request.ContactId,
                    ClientId = request.ClientId,

                    EmailSubject = subjectLine,
                    EmailBody = bodyResult.Content,

                    // 👇 THE TWO THINGS YOU WANTED TO SHOW
                    WebSearchData = webSearchData,
                    FinalPrompt = finalPrompt,

                    // Everything that fed the generation (for transparency/debug UI)
                    Details = new
                    {
                        Model = selectedModel,
                        IsGptModel = isGptModel,
                        SystemPrompt = systemPrompt,               // empty by design (matches frontend)
                        Notes = generationNotes,
                        UseEmails = runtimeReplacements["use_emails"],
                        EmailHistoryEnabled = emailHistoryEnabled,
                        FilledSearchInstructions = filledSearchInstructions,
                        SubjectMode = isAiSubject ? "ai" : "manual",
                        FilledSubjectInstruction = filledSubjectInstruction,
                        ManualSubjectTemplate = manualSubjectTemplate,
                        RuntimeReplacements = runtimeReplacements,
                        CampaignPlaceholderValues = campaignPlaceholderValues
                    },

                    Usage = new
                    {
                        WebSearchTokens = searchResult?.TotalTokens ?? 0,
                        WebSearchCost = searchResult?.CurrentCost ?? 0,
                        BodyTokens = bodyResult.TotalTokens,
                        BodyCost = bodyResult.CurrentCost,
                        SubjectTokens = subjectResult?.TotalTokens ?? 0,
                        SubjectCost = subjectResult?.CurrentCost ?? 0,
                        TotalTokens = (searchResult?.TotalTokens ?? 0) + bodyResult.TotalTokens + (subjectResult?.TotalTokens ?? 0),
                        TotalCost = (searchResult?.CurrentCost ?? 0) + bodyResult.CurrentCost + (subjectResult?.CurrentCost ?? 0)
                    }
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    Message = "Error generating single-contact email",
                    Error = ex.Message
                });
            }
        }

        // ============================================
        // Helpers (self-contained copies)
        // ============================================

        private static readonly JsonSerializerOptions CamelCaseJson = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private static string ApplyPlaceholders(string blueprint, Dictionary<string, string>? values)
        {
            if (string.IsNullOrEmpty(blueprint) || values == null || values.Count == 0)
                return blueprint ?? "";

            string result = blueprint;
            foreach (var (key, value) in values)
            {
                result = Regex.Replace(
                    result,
                    $"{{{Regex.Escape(key)}}}",
                    value ?? "",
                    RegexOptions.IgnoreCase
                );
            }
            return result;
        }

        private static bool ContainsPlaceholder(string? text, string key)
            => !string.IsNullOrEmpty(text) &&
               text.Contains("{" + key + "}", StringComparison.OrdinalIgnoreCase);

        private static string StripHtml(string? input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return "";
            return Regex.Replace(input, "<.*?>", "").Trim();
        }

        private async Task<string> GetEmailConversationContextAsync(int clientId, int contactId)
        {
            try
            {
                var result = await _contactRepository.GetEmailConversationContextAsync(clientId, contactId);
                if (result == null)
                    return "";

                var rawJson = JsonSerializer.Serialize(result, CamelCaseJson);
                using var doc = JsonDocument.Parse(rawJson);
                var root = doc.RootElement;

                if (root.ValueKind != JsonValueKind.Object)
                    return "";

                if (root.TryGetProperty("promptContext", out var pc) &&
                    pc.ValueKind == JsonValueKind.String)
                {
                    return (pc.GetString() ?? "").Trim();
                }

                return "";
            }
            catch
            {
                return "";
            }
        }

        private async Task<string> GetGenerationNotesAsync(int clientId, int contactId)
        {
            try
            {
                var result = await _noteRepository.GetAllNote(clientId, contactId);
                if (result == null)
                    return "";

                var rawJson = JsonSerializer.Serialize(result, CamelCaseJson);
                using var doc = JsonDocument.Parse(rawJson);
                var root = doc.RootElement;

                if (!root.TryGetProperty("success", out var successProp) || !successProp.GetBoolean())
                    return "";

                if (!root.TryGetProperty("data", out var dataProp) || dataProp.ValueKind != JsonValueKind.Array)
                    return "";

                var usableNotes = new List<string>();

                foreach (var item in dataProp.EnumerateArray())
                {
                    var useInGeneration = item.TryGetProperty("isUseInGenration", out var useProp)
                        && useProp.ValueKind == JsonValueKind.True;

                    if (!useInGeneration)
                        continue;

                    var note = item.TryGetProperty("note", out var noteProp)
                        ? noteProp.GetString() ?? ""
                        : "";

                    if (string.IsNullOrWhiteSpace(note))
                        continue;

                    var cleaned = WebUtility.HtmlDecode(note);
                    cleaned = Regex.Replace(cleaned, "<[^>]*>", " ");
                    cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();

                    if (!string.IsNullOrWhiteSpace(cleaned))
                        usableNotes.Add(cleaned);
                }

                return string.Join("\n", usableNotes);
            }
            catch
            {
                return "";
            }
        }

        private static bool IsDeepSeekModel(string? modelName)
            => modelName?.StartsWith("deepseek-", StringComparison.OrdinalIgnoreCase) == true;

        private Task<PitchResult> GeneratePitchByProviderAsync(EnquiryRequest request)
            => IsDeepSeekModel(request.ModelName)
                ? _deepSeekService.GeneratePitchAsync(request)
                : _pitchService.GeneratePitchAsync(request);
    }
}
