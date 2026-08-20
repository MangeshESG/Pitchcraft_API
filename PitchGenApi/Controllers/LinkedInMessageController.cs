using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PitchGenApi.Database;
using PitchGenApi.Interfaces;
using PitchGenApi.Model;
using PitchGenApi.Model.DTOs;
using PitchGenApi.Models;
using PitchGenApi.Services;
using Serilog;

namespace PitchGenApi.Controllers
{
    /// <summary>
    /// LinkedIn message generation and send-tracking.
    ///
    /// Same blueprint + placeholder process as email generation, but:
    ///  • no subject line,
    ///  • plain text, with the blueprint alone deciding how long it runs,
    ///  • only a message the user marks as sent is stored, in linkedin_messages
    ///    (never in contacts.email_body),
    ///  • "sent" is whatever the user ticks — nothing is detected automatically.
    ///
    /// The same table also holds the contact's replies, pasted in by hand
    /// (see import). LinkedIn has no API to sync them, and without them a
    /// follow-up is generated as though nobody ever answered.
    ///
    /// Every route is POST or GET.
    /// </summary>
    [ApiController]
    [Route("api/linkedin-messages")]
    public class LinkedInMessageController : ControllerBase
    {
        private readonly AppDbContext _dbContext;
        private readonly IContactPromptContextService _promptContext;
        private readonly ContactRepository _contactRepository;
        private readonly IPitchService _pitchService;
        private readonly DeepSeekPitchService _deepSeekService;
        private readonly IAiModelSettingsService _aiModelSettings;

        public LinkedInMessageController(
            AppDbContext dbContext,
            IContactPromptContextService promptContext,
            ContactRepository contactRepository,
            IPitchService pitchService,
            DeepSeekPitchService deepSeekService,
            IAiModelSettingsService aiModelSettings)
        {
            _dbContext = dbContext;
            _promptContext = promptContext;
            _contactRepository = contactRepository;
            _pitchService = pitchService;
            _deepSeekService = deepSeekService;
            _aiModelSettings = aiModelSettings;
        }

        // How far back an offline-queued checkbox is still believed.
        private static readonly TimeSpan MaxBackdate = TimeSpan.FromDays(7);

        // Guard on the bulk summary call so a runaway grid can't send 50k ids.
        private const int MaxSummaryContacts = 500;

        // ============================================================
        // 1️⃣  POST api/linkedin-messages/generate
        //     Krafts the message from a blueprint and returns it. Nothing
        //     is stored here - marking it sent is what saves it. Costs one
        //     credit, same as an email.
        // ============================================================
        [HttpPost("generate")]
        public async Task<IActionResult> Generate([FromBody] GenerateLinkedInMessageRequest request)
        {
            try
            {
                if (request == null)
                    return BadRequest(new { Success = false, Message = "Request body is required." });

                if (request.ClientId <= 0)
                    return BadRequest(new { Success = false, Message = "Valid ClientId is required." });

                if (request.ContactId <= 0)
                    return BadRequest(new { Success = false, Message = "Valid ContactId is required." });

                if (request.BlueprintId <= 0)
                    return BadRequest(new { Success = false, Message = "Valid BlueprintId is required." });

                var template = await _dbContext.CampaignTemplates
                    .Include(t => t.TemplateDefinition)
                    .FirstOrDefaultAsync(t =>
                        t.Id == request.BlueprintId &&
                        t.ClientId == request.ClientId.ToString());

                if (template == null)
                    return NotFound(new { Success = false, Message = "Blueprint not found for this client." });

                if (template.TemplateDefinition == null)
                    return StatusCode(500, new { Success = false, Message = "Blueprint definition is missing." });

                var contact = await LoadContactAsync(request.ClientId, request.ContactId);
                if (contact == null)
                    return NotFound(new { Success = false, Message = "Contact not found for this client." });

                // Preview costs nothing, so it doesn't need a credit either.
                if (!request.Preview && !await _contactRepository.HasAvailableCreditAsync(request.ClientId))
                {
                    return Ok(new
                    {
                        Success = false,
                        Message = "No credit is available. Please buy credits to generate a message."
                    });
                }

                var campaignPlaceholderValues =
                    string.IsNullOrWhiteSpace(template.PlaceholderValues)
                        ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        : JsonSerializer.Deserialize<Dictionary<string, string>>(template.PlaceholderValues)
                          ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                var customFields = await _promptContext.GetCustomFieldsAsync(request.ClientId, request.ContactId);
                var insights = await _promptContext.BuildAsync(
                    request.ClientId, request.ContactId, contact.linkedIninformation);

                // ---- runtime replacements (identical set to email generation) ----
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
                    ["linkedin_info"] = insights.ProfessionalSummary,
                    ["professional_summary"] = insights.ProfessionalSummary,
                    ["date"] = DateTime.UtcNow.ToString("MMMM d, yyyy"),
                    ["notes"] = insights.Notes,
                    ["use_email"] = "",
                    ["use_emails"] = "",
                    ["search_output_summary"] = ""
                };

                // Registered up front (empty) so the campaign-level pass can't
                // claim the key — and so the token is cleared rather than left
                // literal when the switch is off. Filled below if it is on.
                runtimeReplacements[PlaceholderEngine.LinkedInHistoryKey] = "";
                runtimeReplacements[PlaceholderEngine.LinkedInConversationKey] = "";

                foreach (var kv in customFields)
                    runtimeReplacements[kv.Key] = kv.Value ?? "";

                // Runtime keys must SURVIVE the campaign-level pass.
                var campaignOnlyValues = campaignPlaceholderValues
                    .Where(kv => !runtimeReplacements.ContainsKey(kv.Key))
                    .ToDictionary(
                        kv => kv.Key,
                        kv => PlaceholderEngine.CleanValue(kv.Key, kv.Value),
                        StringComparer.OrdinalIgnoreCase);

                var campaignBlueprint = PlaceholderEngine.Apply(
                    template.TemplateDefinition.MasterBlueprintUnpopulated ?? "",
                    campaignOnlyValues);

                var hasNotesPlaceholder = PlaceholderEngine.Contains(campaignBlueprint, "notes");
                var hasEmailPlaceholder =
                    PlaceholderEngine.Contains(campaignBlueprint, "use_email") ||
                    PlaceholderEngine.Contains(campaignBlueprint, "use_emails");
                var hasSummaryPlaceholder =
                    PlaceholderEngine.Contains(campaignBlueprint, "linkedin_info") ||
                    PlaceholderEngine.Contains(campaignBlueprint, "professional_summary");

                // {linkedin_messages} — everything already sent to this contact
                // on LinkedIn, so a follow-up doesn't repeat the opener. The
                // blueprint's use_linkedin_message value is the on/off switch;
                // like use_email_history, only an explicit "no" turns it off.
                var hasLinkedInHistoryPlaceholder =
                    PlaceholderEngine.Contains(campaignBlueprint, PlaceholderEngine.LinkedInHistoryKey);

                var linkedInHistoryEnabled = PlaceholderEngine.IsHistoryEnabled(
                    campaignPlaceholderValues, PlaceholderEngine.LinkedInHistoryToggleKey);

                var linkedInHistory = hasLinkedInHistoryPlaceholder && linkedInHistoryEnabled
                    ? await _promptContext.GetSentLinkedInContextAsync(request.ClientId, request.ContactId)
                    : new LinkedInSentContext();

                runtimeReplacements[PlaceholderEngine.LinkedInHistoryKey] = linkedInHistory.Text;

                // {linkedin_conversation} — the same chat, but both sides: what
                // we sent and what they replied, in the order it happened. The
                // slot above stays outbound-only so blueprints already using it
                // are untouched; seeing the replies is opted into by name.
                var hasLinkedInConversationPlaceholder =
                    PlaceholderEngine.Contains(campaignBlueprint, PlaceholderEngine.LinkedInConversationKey);

                var linkedInConversationEnabled = PlaceholderEngine.IsHistoryEnabled(
                    campaignPlaceholderValues, PlaceholderEngine.LinkedInConversationToggleKey);

                var linkedInConversation = hasLinkedInConversationPlaceholder && linkedInConversationEnabled
                    ? await _promptContext.GetLinkedInConversationAsync(request.ClientId, request.ContactId)
                    : new LinkedInConversationContext();

                runtimeReplacements[PlaceholderEngine.LinkedInConversationKey] = linkedInConversation.Text;

                // Email history: only an explicit "no" turns it off.
                var emailHistorySetting =
                    campaignPlaceholderValues.TryGetValue("use_email_history", out var histVal)
                        ? (histVal ?? "").Trim().ToLower()
                        : "";
                var emailHistoryEnabled = emailHistorySetting != "no";

                if (emailHistoryEnabled)
                {
                    runtimeReplacements["use_email"] = insights.EmailContext;
                    runtimeReplacements["use_emails"] = insights.EmailContext;
                }

                var finalPrompt = PlaceholderEngine.Apply(campaignBlueprint, runtimeReplacements);

                // Anything resolved but unplaceholdered is appended, so no input
                // is silently dropped.
                if (!string.IsNullOrWhiteSpace(insights.Notes) && !hasNotesPlaceholder)
                    finalPrompt = PlaceholderEngine.AppendContextSection(
                        finalPrompt,
                        "Notes about this contact (use them to personalize):",
                        insights.Notes);

                if (emailHistoryEnabled && !string.IsNullOrWhiteSpace(insights.EmailContext) && !hasEmailPlaceholder)
                    finalPrompt = PlaceholderEngine.AppendContextSection(
                        finalPrompt,
                        "Previous email conversation with this contact:",
                        insights.EmailContext);

                if (!string.IsNullOrWhiteSpace(insights.ProfessionalSummary) && !hasSummaryPlaceholder)
                    finalPrompt = PlaceholderEngine.AppendContextSection(
                        finalPrompt,
                        "Professional summary (LinkedIn) for this contact:",
                        insights.ProfessionalSummary);

                // No live web search on this path: LinkedIn generation is one
                // model call and one credit. The research already stored on the
                // contact (from a kraft or the Insights panel) is reused, and the
                // slot is always cleared so a literal token never reaches the model.
                var storedResearch = PromptTextCleaner.StripHtml(contact.web_search_data);
                finalPrompt = finalPrompt.Contains("{web_searched_data}")
                    ? finalPrompt.Replace("{web_searched_data}", storedResearch)
                    : finalPrompt;
                runtimeReplacements["search_output_summary"] = storedResearch;

                // Nothing is appended here at all: the blueprint is the only
                // instruction the model gets, and how long the message should run
                // is part of what the blueprint itself says.

                var selectedModel = await _aiModelSettings.GetModelAsync(AiModelPurposes.EmailGeneration);

                if (string.IsNullOrWhiteSpace(selectedModel))
                {
                    selectedModel = !string.IsNullOrWhiteSpace(template.SelectedModel)
                        ? template.SelectedModel
                        : (!string.IsNullOrWhiteSpace(template.TemplateDefinition.SelectedModel)
                            ? template.TemplateDefinition.SelectedModel
                            : AiModelDefaults.EmailGenerationModel);
                }

                var promptSentToAi = finalPrompt;

                var result = await GeneratePitchByProviderAsync(new EnquiryRequest
                {
                    Prompt = promptSentToAi,
                    ScrappedData = "",
                    ModelName = selectedModel
                });

                if (!result.IsSuccess || string.IsNullOrWhiteSpace(result.Content))
                {
                    return StatusCode(500, new
                    {
                        Success = false,
                        Message = "Failed to generate LinkedIn message.",
                        Error = result.Content,
                        FinalPrompt = promptSentToAi
                    });
                }

                // LinkedIn's composer is plain text — markup pasted into it shows
                // up as literal tags.
                var body = PromptTextCleaner.StripHtml(result.Content).Trim();

                // Generation stores nothing. A krafted message is a draft in the
                // user's hands until they tick "sent", and only that tick writes
                // a row (see mark-sent) - the table is a record of what actually
                // went out, not of everything the model ever produced.
                //
                // The uid is minted here so one identity carries from generation
                // through editing to the tick that finally stores it.
                var msgUid = Guid.NewGuid();
                var generatedAt = DateTime.UtcNow;

                if (!request.Preview)
                {
                    // The AI call happened and is billable whether or not the
                    // message is ever sent.
                    await _contactRepository.CreditDeduction(request.ClientId);
                    await _contactRepository.SaveKraftHistoryAsync(
                        request.ContactId, request.ClientId, null, request.BlueprintId, "LinkedIn");
                }

                return Ok(new
                {
                    Success = true,
                    Preview = request.Preview,

                    MessageId = (long?)null,
                    MsgUid = msgUid,
                    ClientId = request.ClientId,
                    ContactId = request.ContactId,
                    BlueprintId = request.BlueprintId,
                    MessageType = LinkedInMessageTypes.Message,

                    Body = body,
                    CharacterCount = body.Length,
                    IsSent = false,
                    SentAt = (DateTime?)null,
                    GeneratedAt = generatedAt,

                    // Same transparency payload the email generator returns, so
                    // the Insights tabs work unchanged on this channel.
                    Notes = insights.Notes,
                    Emails = insights.EmailContext,
                    ProfessionalSummary = insights.ProfessionalSummary,
                    EmailCount = insights.EmailCount,
                    WebSearchData = storedResearch,
                    LinkedInMessages = linkedInHistory.Text,
                    LinkedInMessageCount = linkedInHistory.Count,
                    LinkedInMessagesSentTotal = linkedInHistory.TotalSent,
                    LinkedInConversation = linkedInConversation.Text,
                    LinkedInConversationCount = linkedInConversation.Count,
                    LinkedInConversationReplies = linkedInConversation.InboundCount,
                    FinalPrompt = promptSentToAi,

                    UsedInGeneration = new
                    {
                        Notes = PlaceholderEngine.PromptContains(promptSentToAi, insights.Notes),
                        Emails = emailHistoryEnabled &&
                                 PlaceholderEngine.PromptContains(promptSentToAi, insights.EmailContext),
                        ProfessionalSummary =
                                 PlaceholderEngine.PromptContains(promptSentToAi, insights.ProfessionalSummary),
                        WebSearch = PlaceholderEngine.PromptContains(promptSentToAi, storedResearch),
                        LinkedInMessages = PlaceholderEngine.PromptContains(promptSentToAi, linkedInHistory.Text),
                        LinkedInConversation =
                                 PlaceholderEngine.PromptContains(promptSentToAi, linkedInConversation.Text)
                    },

                    Details = new
                    {
                        Model = selectedModel,
                        EmailHistoryEnabled = emailHistoryEnabled,
                        NotesPlaceholderFound = hasNotesPlaceholder,
                        EmailPlaceholderFound = hasEmailPlaceholder,
                        SummaryPlaceholderFound = hasSummaryPlaceholder,
                        LinkedInHistoryPlaceholderFound = hasLinkedInHistoryPlaceholder,
                        LinkedInHistoryEnabled = linkedInHistoryEnabled,
                        LinkedInConversationPlaceholderFound = hasLinkedInConversationPlaceholder,
                        LinkedInConversationEnabled = linkedInConversationEnabled,
                        UnresolvedPlaceholders = PlaceholderEngine.FindUnresolved(promptSentToAi),
                        RuntimeReplacements = runtimeReplacements,
                        CampaignPlaceholderValues = campaignPlaceholderValues
                    },

                    Usage = new
                    {
                        TotalTokens = result.TotalTokens,
                        TotalCost = result.CurrentCost
                    }
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "LinkedIn message generation failed. ClientId={ClientId}, ContactId={ContactId}",
                    request?.ClientId, request?.ContactId);

                return StatusCode(500, new
                {
                    Success = false,
                    Message = "Error generating LinkedIn message.",
                    Error = ex.Message
                });
            }
        }

        // ============================================================
        // 2️⃣  POST api/linkedin-messages/save
        //     Stores a hand-written message, or saves the user's edits
        //     to a draft before they send it. No credit, no AI call.
        // ============================================================
        [HttpPost("save")]
        public async Task<IActionResult> Save([FromBody] SaveLinkedInMessageRequest request)
        {
            try
            {
                if (request == null || request.ClientId <= 0 || request.ContactId <= 0)
                    return BadRequest(new { Success = false, Message = "Valid ClientId and ContactId are required." });

                if (string.IsNullOrWhiteSpace(request.Body))
                    return BadRequest(new { Success = false, Message = "Body is required." });

                var body = PromptTextCleaner.StripHtml(request.Body).Trim();

                LinkedInMessage? message = null;

                if (request.MessageId.HasValue || request.MsgUid.HasValue)
                {
                    message = await FindMessageAsync(request.ClientId, request.MessageId, request.MsgUid);

                    if (message == null)
                        return NotFound(new { Success = false, Message = "Message not found for this client." });

                    // A sent message is a record of what actually went out —
                    // editing it would rewrite history. Generate a new one instead.
                    if (message.IsSent)
                        return BadRequest(new
                        {
                            Success = false,
                            Message = "This message is already marked as sent and can no longer be edited."
                        });

                    message.Body = body;

                    if (request.BlueprintId.HasValue)
                        message.BlueprintId = request.BlueprintId;
                }
                else
                {
                    var contact = await LoadContactAsync(request.ClientId, request.ContactId);
                    if (contact == null)
                        return NotFound(new { Success = false, Message = "Contact not found for this client." });

                    message = new LinkedInMessage
                    {
                        ClientId = request.ClientId,
                        ContactId = request.ContactId,
                        MessageType = LinkedInMessageTypes.Message,
                        BlueprintId = request.BlueprintId,
                        Body = body,
                        IsSent = false,
                        GeneratedAt = DateTime.UtcNow,
                        MsgUid = request.MsgUid ?? Guid.NewGuid()
                    };

                    _dbContext.LinkedInMessages.Add(message);
                }

                await _dbContext.SaveChangesAsync();

                return Ok(new
                {
                    Success = true,
                    Message = "Saved.",
                    Data = ToDto(message, includeBody: true)
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Saving LinkedIn message failed. ClientId={ClientId}", request?.ClientId);
                return StatusCode(500, new { Success = false, Message = "Error saving message.", Error = ex.Message });
            }
        }

        // ============================================================
        // 3️⃣  POST api/linkedin-messages/mark-sent
        //     THE CHECKBOX, and the only route that stores a message.
        //     Ticking creates the row (or stamps an existing one) with the
        //     server's UTC time; unticking clears it. Nothing is detected
        //     automatically. Safe to call twice - the uid keeps it idempotent.
        // ============================================================
        [HttpPost("mark-sent")]
        public async Task<IActionResult> MarkSent([FromBody] MarkLinkedInMessageSentRequest request)
        {
            try
            {
                if (request == null || request.ClientId <= 0)
                    return BadRequest(new { Success = false, Message = "Valid ClientId is required." });

                // A message ticked for the first time may carry no identifier at
                // all: nothing has been stored for it yet, so the body is what
                // the row gets built from.
                var identified = request.MessageId.HasValue || request.MsgUid.HasValue;

                if (!identified && string.IsNullOrWhiteSpace(request.Body))
                    return BadRequest(new { Success = false, Message = "MessageId, MsgUid or Body is required." });

                var message = identified
                    ? await FindMessageAsync(request.ClientId, request.MessageId, request.MsgUid)
                    : null;

                if (message == null)
                {
                    // Generation stores nothing, so the first tick is what
                    // creates the row - the body travels with it.
                    if (!request.IsSent)
                        return Ok(new
                        {
                            Success = true,
                            Message = "Nothing stored for this message.",
                            Data = (object?)null
                        });

                    if (string.IsNullOrWhiteSpace(request.Body))
                        return NotFound(new { Success = false, Message = "Message not found for this client." });

                    if (request.ContactId <= 0)
                        return BadRequest(new { Success = false, Message = "Valid ContactId is required to store this message." });

                    var contact = await LoadContactAsync(request.ClientId, request.ContactId);

                    if (contact == null)
                        return NotFound(new { Success = false, Message = "Contact not found for this client." });

                    message = new LinkedInMessage
                    {
                        ClientId = request.ClientId,
                        ContactId = request.ContactId,
                        MessageType = LinkedInMessageTypes.Message,
                        BlueprintId = request.BlueprintId,
                        Body = PromptTextCleaner.StripHtml(request.Body).Trim(),
                        IsSent = true,
                        SentAt = ResolveSentAt(request.OccurredAtUtc),
                        MarkedFrom = NormalizeSource(request.MarkedFrom),
                        GeneratedAt = DateTime.UtcNow,
                        MsgUid = request.MsgUid ?? Guid.NewGuid()
                    };

                    _dbContext.LinkedInMessages.Add(message);
                    await _dbContext.SaveChangesAsync();

                    return Ok(new
                    {
                        Success = true,
                        Message = "Marked as sent.",
                        Data = ToDto(message, includeBody: false)
                    });
                }

                if (request.IsSent)
                {
                    // Already ticked: keep the original timestamp. A retry or a
                    // double tap must not move the send time.
                    if (!message.IsSent)
                    {
                        // Edits made between generating and ticking are part of
                        // what actually went out.
                        if (!string.IsNullOrWhiteSpace(request.Body))
                            message.Body = PromptTextCleaner.StripHtml(request.Body).Trim();

                        message.IsSent = true;
                        message.SentAt = ResolveSentAt(request.OccurredAtUtc);
                        message.MarkedFrom = NormalizeSource(request.MarkedFrom);
                        await _dbContext.SaveChangesAsync();
                    }
                }
                else
                {
                    if (message.IsSent)
                    {
                        message.IsSent = false;
                        message.SentAt = null;
                        message.MarkedFrom = null;
                        await _dbContext.SaveChangesAsync();
                    }
                }

                return Ok(new
                {
                    Success = true,
                    Message = message.IsSent ? "Marked as sent." : "Marked as not sent.",
                    Data = ToDto(message, includeBody: false)
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Marking LinkedIn message sent failed. ClientId={ClientId}", request?.ClientId);
                return StatusCode(500, new { Success = false, Message = "Error updating message.", Error = ex.Message });
            }
        }

        // ============================================================
        // 4️⃣  POST api/linkedin-messages/import
        //     A message that happened on LinkedIn outside Pitchkraft,
        //     pasted in by hand. Free: no model runs, so no credit.
        // ============================================================
        [HttpPost("import")]
        public async Task<IActionResult> Import([FromBody] ImportLinkedInMessageRequest request)
        {
            try
            {
                if (request == null || request.ClientId <= 0 || request.ContactId <= 0)
                    return BadRequest(new { Success = false, Message = "Valid ClientId and ContactId are required." });

                var body = PromptTextCleaner.StripHtml(request.Body ?? "").Trim();

                if (string.IsNullOrWhiteSpace(body))
                    return BadRequest(new { Success = false, Message = "Body is required." });

                if (body.Length > MaxImportLength)
                    return BadRequest(new
                    {
                        Success = false,
                        Message = $"That paste is {body.Length:N0} characters. Paste one message at a time — the limit is {MaxImportLength:N0}."
                    });

                var contact = await LoadContactAsync(request.ClientId, request.ContactId);
                if (contact == null)
                    return NotFound(new { Success = false, Message = "Contact not found for this client." });

                var direction = LinkedInMessageDirections.Normalize(
                    string.IsNullOrWhiteSpace(request.Direction)
                        ? LinkedInMessageDirections.Inbound
                        : request.Direction);

                var hash = HashBody(body);

                // People re-paste the same chat every time they check it. Answer
                // the second paste with the row the first one made, so the model
                // never reads one reply as five.
                var existing = await FindByHashAsync(
                    request.ClientId, request.ContactId, direction, hash);

                if (existing != null)
                {
                    return Ok(new
                    {
                        Success = true,
                        Created = false,
                        Message = "That message is already saved against this contact.",
                        Data = ToDto(existing, includeBody: true)
                    });
                }

                var message = new LinkedInMessage
                {
                    ClientId = request.ClientId,
                    ContactId = request.ContactId,
                    Direction = direction,
                    MessageType = LinkedInMessageTypes.Message,
                    BlueprintId = null,
                    Body = body,
                    BodyHash = hash,

                    // Not a draft: a message someone pasted already happened, in
                    // whichever direction. That is what puts it in the
                    // conversation the generators read.
                    IsSent = true,
                    SentAt = ResolveSentAt(request.OccurredAtUtc),
                    MarkedFrom = NormalizeSource(request.Source),
                    GeneratedAt = DateTime.UtcNow,
                    MsgUid = Guid.NewGuid()
                };

                _dbContext.LinkedInMessages.Add(message);

                try
                {
                    await _dbContext.SaveChangesAsync();
                }
                catch (DbUpdateException)
                {
                    // Two tabs pasting the same text at once: the unique index
                    // caught it. Return the row that won rather than an error -
                    // the caller's message is stored either way, which is all
                    // they asked for.
                    _dbContext.Entry(message).State = EntityState.Detached;

                    var raced = await FindByHashAsync(
                        request.ClientId, request.ContactId, direction, hash);

                    if (raced == null)
                        throw;

                    return Ok(new
                    {
                        Success = true,
                        Created = false,
                        Message = "That message is already saved against this contact.",
                        Data = ToDto(raced, includeBody: true)
                    });
                }

                return Ok(new
                {
                    Success = true,
                    Created = true,
                    Message = direction == LinkedInMessageDirections.Inbound
                        ? "Reply saved."
                        : "Message saved.",
                    Data = ToDto(message, includeBody: true)
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Importing LinkedIn message failed. ClientId={ClientId}", request?.ClientId);
                return StatusCode(500, new { Success = false, Message = "Error saving message.", Error = ex.Message });
            }
        }

        // ============================================================
        // 5️⃣  GET api/linkedin-messages/by-contact
        //     The contact's LinkedIn history, newest first. Reads
        //     straight down the clustered key — one range seek.
        // ============================================================
        [HttpGet("by-contact")]
        public async Task<IActionResult> GetByContact(
            [FromQuery] int clientId,
            [FromQuery] int contactId,
            [FromQuery] bool includeBody = true,
            [FromQuery] string? messageType = null,
            [FromQuery] string? direction = null,
            [FromQuery] bool sentOnly = false,
            [FromQuery] int take = 50)
        {
            try
            {
                if (clientId <= 0 || contactId <= 0)
                    return BadRequest(new { Success = false, Message = "Valid clientId and contactId are required." });

                take = Math.Clamp(take, 1, 200);

                var query = _dbContext.LinkedInMessages
                    .AsNoTracking()
                    .Where(m => m.ClientId == clientId && m.ContactId == contactId);

                if (!string.IsNullOrWhiteSpace(messageType))
                {
                    var normalized = LinkedInMessageTypes.Normalize(messageType);
                    query = query.Where(m => m.MessageType == normalized);
                }

                if (!string.IsNullOrWhiteSpace(direction))
                {
                    var wanted = LinkedInMessageDirections.Normalize(direction);
                    query = query.Where(m => m.Direction == wanted);
                }

                if (sentOnly)
                    query = query.Where(m => m.IsSent);

                var rows = await query
                    .OrderByDescending(m => m.Id)
                    .Take(take)
                    .ToListAsync();

                return Ok(new
                {
                    Success = true,
                    ClientId = clientId,
                    ContactId = contactId,
                    Count = rows.Count,
                    SentCount = rows.Count(m => m.IsSent),
                    LastSentAt = rows.Where(m => m.IsSent).Max(m => (DateTime?)m.SentAt),
                    Data = rows.Select(m => ToDto(m, includeBody)).ToList()
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Fetching LinkedIn messages failed. ClientId={ClientId}, ContactId={ContactId}",
                    clientId, contactId);
                return StatusCode(500, new { Success = false, Message = "Error fetching messages.", Error = ex.Message });
            }
        }

        // ============================================================
        // 6️⃣  GET api/linkedin-messages/detail
        //     One message with its full body.
        // ============================================================
        [HttpGet("detail")]
        public async Task<IActionResult> GetDetail(
            [FromQuery] int clientId,
            [FromQuery] long? id = null,
            [FromQuery] Guid? msgUid = null)
        {
            try
            {
                if (clientId <= 0)
                    return BadRequest(new { Success = false, Message = "Valid clientId is required." });

                if (!id.HasValue && !msgUid.HasValue)
                    return BadRequest(new { Success = false, Message = "id or msgUid is required." });

                var message = await FindMessageAsync(clientId, id, msgUid, tracking: false);

                if (message == null)
                    return NotFound(new { Success = false, Message = "Message not found for this client." });

                return Ok(new { Success = true, Data = ToDto(message, includeBody: true) });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Fetching LinkedIn message detail failed. ClientId={ClientId}", clientId);
                return StatusCode(500, new { Success = false, Message = "Error fetching message.", Error = ex.Message });
            }
        }

        // ============================================================
        // 7️⃣  GET api/linkedin-messages/sent
        //     Everything this client has ticked as sent, newest first.
        //     Served by the filtered index on (client_id, sent_at).
        // ============================================================
        [HttpGet("sent")]
        public async Task<IActionResult> GetSentHistory(
            [FromQuery] int clientId,
            [FromQuery] DateTime? fromUtc = null,
            [FromQuery] DateTime? toUtc = null,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 50)
        {
            try
            {
                if (clientId <= 0)
                    return BadRequest(new { Success = false, Message = "Valid clientId is required." });

                page = Math.Max(page, 1);
                pageSize = Math.Clamp(pageSize, 1, 200);

                var query = _dbContext.LinkedInMessages
                    .AsNoTracking()
                    .Where(m => m.ClientId == clientId && m.IsSent);

                if (fromUtc.HasValue)
                    query = query.Where(m => m.SentAt >= fromUtc.Value);

                if (toUtc.HasValue)
                    query = query.Where(m => m.SentAt <= toUtc.Value);

                var total = await query.CountAsync();

                // Joined to contacts so the history screen can show a name
                // without a second round trip per row.
                var rows = await query
                    .OrderByDescending(m => m.SentAt)
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .Join(_dbContext.contacts.AsNoTracking(),
                        m => m.ContactId,
                        c => c.id,
                        (m, c) => new
                        {
                            m.Id,
                            m.MsgUid,
                            m.ContactId,
                            ContactName = c.full_name,
                            c.company_name,
                            c.linkedin_url,
                            m.MessageType,
                            m.BlueprintId,
                            m.SentAt,
                            m.MarkedFrom,
                            m.GeneratedAt
                        })
                    .ToListAsync();

                return Ok(new
                {
                    Success = true,
                    ClientId = clientId,
                    Page = page,
                    PageSize = pageSize,
                    Total = total,
                    Data = rows
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Fetching LinkedIn sent history failed. ClientId={ClientId}", clientId);
                return StatusCode(500, new { Success = false, Message = "Error fetching sent history.", Error = ex.Message });
            }
        }

        // ============================================================
        // 8️⃣  POST api/linkedin-messages/summary
        //     Badges for a whole grid page in ONE query, so the contact
        //     list never fires a request per row.
        // ============================================================
        [HttpPost("summary")]
        public async Task<IActionResult> GetSummary([FromBody] LinkedInMessageSummaryRequest request)
        {
            try
            {
                if (request == null || request.ClientId <= 0)
                    return BadRequest(new { Success = false, Message = "Valid ClientId is required." });

                var contactIds = (request.ContactIds ?? new List<int>())
                    .Where(id => id > 0)
                    .Distinct()
                    .ToList();

                if (contactIds.Count == 0)
                    return Ok(new { Success = true, Data = Array.Empty<object>() });

                if (contactIds.Count > MaxSummaryContacts)
                    return BadRequest(new
                    {
                        Success = false,
                        Message = $"At most {MaxSummaryContacts} contact ids per call."
                    });

                var rows = await _dbContext.LinkedInMessages
                    .AsNoTracking()
                    .Where(m => m.ClientId == request.ClientId && contactIds.Contains(m.ContactId))
                    .GroupBy(m => m.ContactId)
                    // Sum-of-case rather than Count(predicate), and a plain Max
                    // over sent_at (which is NULL on drafts, and SQL's MAX skips
                    // NULLs) — both forms translate to SQL on every provider
                    // version, so this stays one GROUP BY instead of silently
                    // falling back to client evaluation.
                    .Select(g => new
                    {
                        ContactId = g.Key,
                        TotalCount = g.Count(),
                        SentCount = g.Sum(m => m.IsSent ? 1 : 0),
                        DraftCount = g.Sum(m => m.IsSent ? 0 : 1),
                        LastSentAt = g.Max(m => m.SentAt),
                        LastGeneratedAt = g.Max(m => m.GeneratedAt)
                    })
                    .ToListAsync();

                return Ok(new { Success = true, ClientId = request.ClientId, Data = rows });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Fetching LinkedIn summary failed. ClientId={ClientId}", request?.ClientId);
                return StatusCode(500, new { Success = false, Message = "Error fetching summary.", Error = ex.Message });
            }
        }

        // ============================================================
        // 9️⃣  POST api/linkedin-messages/delete
        //     Removes one message from a contact's record: a draft, a
        //     message stored as sent, or a pasted reply.
        //
        //     This used to refuse anything already marked as sent, on the
        //     grounds that history should not be rewritten. That guard
        //     assumed the sent mark was a checkbox the user could untick.
        //     It isn't any more - storing a message is a single click, and a
        //     pasted reply is stored the moment it is saved - so refusing
        //     would leave a mistake on the record with no way to take it
        //     back. Deleting one row by id stays a deliberate act, and the
        //     caller is expected to confirm it first.
        // ============================================================
        [HttpPost("delete")]
        public async Task<IActionResult> Delete([FromBody] DeleteLinkedInMessageRequest request)
        {
            try
            {
                if (request == null || request.ClientId <= 0)
                    return BadRequest(new { Success = false, Message = "Valid ClientId is required." });

                if (!request.MessageId.HasValue && !request.MsgUid.HasValue)
                    return BadRequest(new { Success = false, Message = "MessageId or MsgUid is required." });

                var message = await FindMessageAsync(request.ClientId, request.MessageId, request.MsgUid);

                if (message == null)
                    return NotFound(new { Success = false, Message = "Message not found for this client." });

                var wasInbound = string.Equals(
                    message.Direction, LinkedInMessageDirections.Inbound, StringComparison.OrdinalIgnoreCase);

                _dbContext.LinkedInMessages.Remove(message);
                await _dbContext.SaveChangesAsync();

                // Worth logging: this is the one call that removes something the
                // generators were reading as fact about the conversation.
                Log.Information(
                    "Deleted LinkedIn message {MessageId} ({Direction}) for ClientId={ClientId}, ContactId={ContactId}.",
                    message.Id, message.Direction, message.ClientId, message.ContactId);

                return Ok(new
                {
                    Success = true,
                    Message = wasInbound ? "Reply deleted." : "Message deleted."
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Deleting LinkedIn message failed. ClientId={ClientId}", request?.ClientId);
                return StatusCode(500, new { Success = false, Message = "Error deleting message.", Error = ex.Message });
            }
        }

        // ============================================================
        // Helpers
        // ============================================================

        private Task<LinkedInMessage?> FindMessageAsync(
            int clientId, long? id, Guid? msgUid, bool tracking = true)
        {
            var query = tracking
                ? _dbContext.LinkedInMessages.AsQueryable()
                : _dbContext.LinkedInMessages.AsNoTracking();

            // Both filters carry client_id so a guessed id can never reach
            // another client's row, and both hit an index.
            return msgUid.HasValue
                ? query.FirstOrDefaultAsync(m => m.ClientId == clientId && m.MsgUid == msgUid.Value)
                : query.FirstOrDefaultAsync(m => m.ClientId == clientId && m.Id == id!.Value);
        }

        // A single LinkedIn message, not a whole pasted thread. Auto-splitting a
        // copied chat is not attempted: the text varies by locale and by app
        // version, and a wrong split feeds the model a conversation that never
        // happened - worse than having none.
        private const int MaxImportLength = 8000;

        /// <summary>
        /// Whitespace-collapsed, case-folded SHA-256. Normalizing first is what
        /// makes the dedupe survive the trailing newline or stray indent a paste
        /// picks up on the way through the clipboard.
        /// </summary>
        private static byte[] HashBody(string body)
        {
            var normalized = string.Join(' ',
                body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                .ToLowerInvariant();

            return SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        }

        /// <summary>
        /// Scoped to one direction, matching the filtered unique index. The same
        /// words quoted back in the other direction are a different event in the
        /// conversation, not a duplicate of it.
        /// </summary>
        private Task<LinkedInMessage?> FindByHashAsync(
            int clientId, int contactId, string direction, byte[] hash)
            => _dbContext.LinkedInMessages
                .AsNoTracking()
                .FirstOrDefaultAsync(m =>
                    m.ClientId == clientId &&
                    m.ContactId == contactId &&
                    m.Direction == direction &&
                    m.BodyHash == hash);

        private Task<Contact?> LoadContactAsync(int clientId, int contactId)
            => _dbContext.contacts
                .AsNoTracking()
                .Include(c => c.data_file)
                .FirstOrDefaultAsync(c => c.id == contactId && c.data_file.client_id == clientId);

        /// <summary>
        /// The send time is the server's UTC clock. A client-supplied time is
        /// only honoured for a checkbox that was queued offline: it has to be in
        /// the past and recent, otherwise a wrong device clock would write a
        /// nonsense date.
        /// </summary>
        private static DateTime ResolveSentAt(DateTime? occurredAtUtc)
        {
            var now = DateTime.UtcNow;

            if (!occurredAtUtc.HasValue)
                return now;

            var supplied = occurredAtUtc.Value.Kind == DateTimeKind.Local
                ? occurredAtUtc.Value.ToUniversalTime()
                : DateTime.SpecifyKind(occurredAtUtc.Value, DateTimeKind.Utc);

            if (supplied > now || supplied < now - MaxBackdate)
                return now;

            return supplied;
        }

        private static string? NormalizeSource(string? source)
        {
            var value = (source ?? "").Trim().ToLowerInvariant();
            return value is "extension" or "web" ? value : "web";
        }

        private static object ToDto(LinkedInMessage m, bool includeBody) => new
        {
            m.Id,
            m.MsgUid,
            m.ClientId,
            m.ContactId,
            m.Direction,
            m.MessageType,
            m.BlueprintId,
            Body = includeBody ? m.Body : null,
            CharacterCount = m.Body?.Length ?? 0,
            m.IsSent,
            m.SentAt,
            m.MarkedFrom,
            m.GeneratedAt
        };

        private static bool IsDeepSeekModel(string? modelName)
            => modelName?.StartsWith("deepseek-", StringComparison.OrdinalIgnoreCase) == true;

        private Task<PitchResult> GeneratePitchByProviderAsync(EnquiryRequest request)
            => IsDeepSeekModel(request.ModelName)
                ? _deepSeekService.GeneratePitchAsync(request)
                : _pitchService.GeneratePitchAsync(request);
    }
}
