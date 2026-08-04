using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PitchGenApi.Database;
using PitchGenApi.Interfaces;
using PitchGenApi.Model;
using PitchGenApi.Model.DTOs;
using PitchGenApi.Models;
using PitchGenApi.Services;
using System.Net;
using System.Text;
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
        //    Returns the email + every input that fed the generation:
        //    notes, email conversation, professional (LinkedIn) summary,
        //    web-search data and the final prompt.
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

                // Already krafted → still return the personalization inputs so the
                // UI can show Notes / Emails / Professional summary without a re-kraft.
                if (!request.OverwriteExisting && !string.IsNullOrWhiteSpace(contact.email_body))
                {
                    var existingInsights = await BuildContactInsightsAsync(
                        parsedClientId, contact.id, contact.linkedIninformation);

                    return Ok(new
                    {
                        Success = true,
                        Message = "Email already exists for this contact",
                        Generated = false,
                        ContactId = contact.id,
                        EmailSubject = contact.email_subject,
                        EmailBody = contact.email_body,

                        Notes = existingInsights.Notes,
                        Emails = existingInsights.EmailContext,
                        ProfessionalSummary = existingInsights.ProfessionalSummary,
                        EmailCount = existingInsights.EmailCount
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

                // ============================================================
                // 1️⃣ RESOLVE ALL PERSONALIZATION INPUTS UP FRONT
                //    (notes + email conversation + professional summary)
                //    These are always resolved so they can be both USED in the
                //    prompt and RETURNED to the UI.
                // ============================================================
                var insights = await BuildContactInsightsAsync(
                    parsedClientId, contact.id, contact.linkedIninformation);

                var generationNotes = insights.Notes;
                var emailConversation = insights.EmailContext;
                var professionalSummary = insights.ProfessionalSummary;

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
                    ["linkedin_info"] = professionalSummary,
                    ["professional_summary"] = professionalSummary,   // alias
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

                // ---- which placeholders does the blueprint actually contain? ----
                var hasNotesPlaceholder = ContainsPlaceholder(campaignBlueprint, "notes");
                var hasEmailPlaceholder =
                    ContainsPlaceholder(campaignBlueprint, "use_email") ||
                    ContainsPlaceholder(campaignBlueprint, "use_emails");
                var hasSummaryPlaceholder =
                    ContainsPlaceholder(campaignBlueprint, "linkedin_info") ||
                    ContainsPlaceholder(campaignBlueprint, "professional_summary");

                // ---- email history: only an explicit "no" turns it off ----
                var emailHistorySetting =
                    campaignPlaceholderValues.TryGetValue("use_email_history", out var histVal)
                        ? (histVal ?? "").Trim().ToLower()
                        : "";

                var emailHistoryEnabled = emailHistorySetting != "no";

                if (emailHistoryEnabled)
                {
                    runtimeReplacements["use_email"] = emailConversation;
                    runtimeReplacements["use_emails"] = emailConversation;
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

                // ============================================================
                // 2️⃣ MAKE SURE EVERY RESOLVED INPUT REACHES THE MODEL
                //    If the blueprint has no placeholder for a value, append it
                //    as a labelled context block instead of dropping it.
                // ============================================================
                var notesUsed = false;
                var emailsUsed = false;
                var summaryUsed = false;

                if (!string.IsNullOrWhiteSpace(generationNotes))
                {
                    notesUsed = true;
                    if (!hasNotesPlaceholder)
                        finalPrompt = AppendContextSection(
                            finalPrompt,
                            "Notes about this contact (use them to personalize):",
                            generationNotes);
                }

                if (emailHistoryEnabled && !string.IsNullOrWhiteSpace(emailConversation))
                {
                    emailsUsed = true;
                    if (!hasEmailPlaceholder)
                        finalPrompt = AppendContextSection(
                            finalPrompt,
                            "Previous email conversation with this contact:",
                            emailConversation);
                }

                if (!string.IsNullOrWhiteSpace(professionalSummary))
                {
                    summaryUsed = true;
                    if (!hasSummaryPlaceholder)
                        finalPrompt = AppendContextSection(
                            finalPrompt,
                            "Professional summary (LinkedIn) for this contact:",
                            professionalSummary);
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
                                ModelName = AiModelDefaults.WebSearchModel
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
                        WebSearchData = webSearchData,
                        Notes = generationNotes,
                        Emails = emailConversation,
                        ProfessionalSummary = professionalSummary
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

                    // 👇 EVERYTHING THE UI SHOWS IN THE INSIGHTS TABS
                    WebSearchData = webSearchData,
                    FinalPrompt = finalPrompt,
                    Notes = generationNotes,
                    Emails = emailConversation,
                    ProfessionalSummary = professionalSummary,
                    EmailCount = insights.EmailCount,

                    // Which inputs actually reached the model
                    UsedInGeneration = new
                    {
                        Notes = notesUsed,
                        Emails = emailsUsed,
                        ProfessionalSummary = summaryUsed,
                        WebSearch = !string.IsNullOrWhiteSpace(webSearchData)
                    },

                    // Everything that fed the generation (for transparency/debug UI)
                    Details = new
                    {
                        Model = selectedModel,
                        IsGptModel = isGptModel,
                        SystemPrompt = systemPrompt,               // empty by design (matches frontend)
                        Notes = generationNotes,
                        UseEmails = runtimeReplacements["use_emails"],
                        LinkedinInfo = professionalSummary,
                        EmailHistoryEnabled = emailHistoryEnabled,
                        NotesPlaceholderFound = hasNotesPlaceholder,
                        EmailPlaceholderFound = hasEmailPlaceholder,
                        SummaryPlaceholderFound = hasSummaryPlaceholder,
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
        // 📄 INSIGHTS FOR AN ALREADY-KRAFTED CONTACT
        //    Same notes / emails / professional summary the generator used,
        //    without re-generating (no credit, no LLM call).
        //    GET api/email-generation/insights?clientId=1&contactId=2
        // ============================================
        [HttpGet("insights")]
        public async Task<IActionResult> GetContactInsights(
            [FromQuery] int clientId,
            [FromQuery] int contactId)
        {
            try
            {
                if (clientId <= 0)
                    return BadRequest(new { Message = "Valid clientId is required" });

                if (contactId <= 0)
                    return BadRequest(new { Message = "Valid contactId is required" });

                var contact = await _dbContext.contacts
                    .Include(c => c.data_file)
                    .FirstOrDefaultAsync(c =>
                        c.id == contactId &&
                        c.data_file.client_id == clientId);

                if (contact == null)
                    return NotFound(new { Message = "Contact not found" });

                var insights = await BuildContactInsightsAsync(
                    clientId, contactId, contact.linkedIninformation);

                return Ok(new
                {
                    Success = true,
                    ContactId = contactId,
                    ClientId = clientId,
                    HasKraftedEmail = !string.IsNullOrWhiteSpace(contact.email_body),
                    EmailSubject = contact.email_subject ?? "",
                    EmailBody = contact.email_body ?? "",

                    Notes = insights.Notes,
                    Emails = insights.EmailContext,
                    ProfessionalSummary = insights.ProfessionalSummary,
                    EmailCount = insights.EmailCount,

                    HasNotes = !string.IsNullOrWhiteSpace(insights.Notes),
                    HasEmails = !string.IsNullOrWhiteSpace(insights.EmailContext),
                    HasProfessionalSummary = !string.IsNullOrWhiteSpace(insights.ProfessionalSummary)
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    Message = "Error fetching contact insights",
                    Error = ex.Message
                });
            }
        }

        // ============================================
        // Insight resolution
        // ============================================

        private sealed class ContactInsights
        {
            public string Notes { get; set; } = "";
            public string EmailContext { get; set; } = "";
            public int EmailCount { get; set; }
            public string ProfessionalSummary { get; set; } = "";
        }

        // Resolves the three personalization inputs for a contact. Notes and the
        // email conversation come from their repositories; the professional
        // summary is the contact's stored LinkedIn summary, HTML-stripped.
        private async Task<ContactInsights> BuildContactInsightsAsync(
            int clientId,
            int contactId,
            string? linkedinInformation)
        {
            var notesTask = GetGenerationNotesAsync(clientId, contactId);
            var emailTask = GetEmailConversationContextAsync(clientId, contactId);

            await Task.WhenAll(notesTask, emailTask);

            var emailContext = emailTask.Result;

            return new ContactInsights
            {
                Notes = notesTask.Result,
                EmailContext = emailContext.Text,
                EmailCount = emailContext.Count,
                ProfessionalSummary = StripHtml(linkedinInformation)
            };
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
                var replacement = value ?? "";

                // MatchEvaluator (not the string overload) so "$" inside notes,
                // email bodies or LinkedIn summaries isn't treated as a regex
                // substitution token.
                result = Regex.Replace(
                    result,
                    $"{{{Regex.Escape(key)}}}",
                    _ => replacement,
                    RegexOptions.IgnoreCase
                );
            }
            return result;
        }

        private static bool ContainsPlaceholder(string? text, string key)
            => !string.IsNullOrEmpty(text) &&
               text.Contains("{" + key + "}", StringComparison.OrdinalIgnoreCase);

        private static string AppendContextSection(string prompt, string label, string content)
            => string.IsNullOrWhiteSpace(content)
                ? prompt
                : $"{prompt}\n\n{label}\n{content.Trim()}";

        // Strips markup but keeps paragraph breaks, so multi-line LinkedIn
        // summaries and notes stay readable in the prompt and in the UI.
        private static string StripHtml(string? input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return "";

            var text = Regex.Replace(input, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"</(p|div|li|tr|h[1-6])\s*>", "\n", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, "<[^>]*>", "");
            text = WebUtility.HtmlDecode(text);
            text = text.Replace('\u00A0', ' ');   // non-breaking space
            text = Regex.Replace(text, @"[ \t]+", " ");
            text = Regex.Replace(text, @"\n{3,}", "\n\n");

            return text.Trim();
        }

        private sealed class EmailContextResult
        {
            public string Text { get; set; } = "";
            public int Count { get; set; }
        }

        private async Task<EmailContextResult> GetEmailConversationContextAsync(int clientId, int contactId)
        {
            var empty = new EmailContextResult();

            try
            {
                var result = await _contactRepository.GetEmailConversationContextAsync(clientId, contactId);
                if (result == null)
                    return empty;

                var rawJson = JsonSerializer.Serialize(result, CamelCaseJson);
                using var doc = JsonDocument.Parse(rawJson);
                var root = doc.RootElement;

                if (root.ValueKind != JsonValueKind.Object)
                    return empty;

                var emailCount = 0;
                var hasEmailsArray =
                    root.TryGetProperty("emails", out var emailsProp) &&
                    emailsProp.ValueKind == JsonValueKind.Array;

                if (hasEmailsArray)
                    emailCount = emailsProp.GetArrayLength();

                // Preferred: the repository's ready-made prompt context.
                if (root.TryGetProperty("promptContext", out var pc) &&
                    pc.ValueKind == JsonValueKind.String)
                {
                    var promptContext = (pc.GetString() ?? "").Trim();
                    if (!string.IsNullOrWhiteSpace(promptContext))
                        return new EmailContextResult { Text = promptContext, Count = emailCount };
                }

                // Fallback: build a readable thread from the raw emails.
                if (!hasEmailsArray || emailCount == 0)
                    return empty;

                var builder = new StringBuilder();
                var index = 0;

                foreach (var email in emailsProp.EnumerateArray())
                {
                    index++;

                    if (builder.Length > 0)
                        builder.Append("\n\n---\n\n");

                    var direction = ReadStringProperty(email, "direction") == "Sent"
                        ? "SENT BY US"
                        : "RECEIVED FROM CONTACT";

                    builder.Append($"Message {index} - {direction}");

                    AppendEmailLine(builder, email, "sentAt", "Sent");
                    AppendEmailLine(builder, email, "senderEmailId", "From");
                    AppendEmailLine(builder, email, "toEmail", "To");
                    AppendEmailLine(builder, email, "subject", "Subject");

                    var body = ReadStringProperty(email, "body");
                    if (!string.IsNullOrWhiteSpace(body))
                        builder.Append($"\nEmail Body:\n{StripHtml(body)}");
                }

                return new EmailContextResult
                {
                    Text = builder.ToString().Trim(),
                    Count = emailCount
                };
            }
            catch
            {
                return empty;
            }
        }

        private static void AppendEmailLine(StringBuilder builder, JsonElement email, string property, string label)
        {
            var value = ReadStringProperty(email, property);
            if (!string.IsNullOrWhiteSpace(value))
                builder.Append($"\n{label}: {value.Trim()}");
        }

        private static string ReadStringProperty(JsonElement element, string property)
        {
            if (element.ValueKind != JsonValueKind.Object)
                return "";

            if (!element.TryGetProperty(property, out var prop))
                return "";

            return prop.ValueKind switch
            {
                JsonValueKind.String => prop.GetString() ?? "",
                JsonValueKind.Number => prop.ToString(),
                _ => ""
            };
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

                    var cleaned = StripHtml(note);

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
