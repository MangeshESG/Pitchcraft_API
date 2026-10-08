using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PitchGenApi.Database;
using PitchGenApi.Interfaces;
using PitchGenApi.Model;
using PitchGenApi.Model.DTOs;
using PitchGenApi.Models;
using PitchGenApi.Services;
using Serilog;
using System.Net;
using System.Text;
using System.Globalization;
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
        private readonly QwenPitchService _qwenService;
        private readonly IAiModelSettingsService _aiModelSettings;
        private readonly IContactPromptContextService _promptContext;
        private readonly IUnsubscribeRepository _unsubscribeRepository;

        /// <summary>
        /// Blueprint slot that receives the campaign's subject rules, so the
        /// subject can be written in the same call as the body.
        /// </summary>
        private const string SubjectInstructionsKey = "subject_instructions";

        public EmailGenerationController(
            AppDbContext dbContext,
            IPitchService pitchService,
            ContactRepository contactRepository,
            INoteRepository noteRepository,
            DeepSeekPitchService deepSeekService,
            QwenPitchService qwenService,
            IAiModelSettingsService aiModelSettings,
            IContactPromptContextService promptContext,
            IUnsubscribeRepository unsubscribeRepository)
        {
            _dbContext = dbContext;
            _pitchService = pitchService;
            _contactRepository = contactRepository;
            _noteRepository = noteRepository;
            _deepSeekService = deepSeekService;
            _qwenService = qwenService;
            _aiModelSettings = aiModelSettings;
            _promptContext = promptContext;
            _unsubscribeRepository = unsubscribeRepository;
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
                        EmailHighlights = ReadStoredHighlights(contact.email_highlights),

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

                // Scoped to THIS client's field definitions. A contact that has
                // moved between clients keeps the old client's values, and two
                // clients' seeded fields share names ("Status", "Contact type",
                // …) — without the client filter those collide when keyed by
                // name below.
                var customFieldRows = await (
                    from value in _dbContext.contact_custom_field_values
                    join field in _dbContext.crm_custom_fields
                        on value.field_id equals field.id
                    where value.contact_id == request.ContactId
                       && field.client_id == parsedClientId
                    select new { field.field_name, value.value }
                ).ToListAsync();

                // Grouped rather than ToDictionary: nothing stops one client
                // holding two fields of the same name, and a duplicate must not
                // fail the whole generation. First non-empty value wins.
                var customFields = customFieldRows
                    .GroupBy(x => x.field_name, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        g => g.Key,
                        g => g.Select(x => x.value).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "",
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

                // {linkedin_messages}. Registered empty up front so the
                // campaign-level pass can't claim the key, and so the token is
                // cleared rather than left literal when the switch is off.
                runtimeReplacements[PlaceholderEngine.LinkedInHistoryKey] = "";
                runtimeReplacements[PlaceholderEngine.LinkedInConversationKey] = "";

                // {unsubscribe_url}. Registered here for the same reason as the
                // keys above: the campaign-level pass must not claim it, and a
                // blueprint that asks for it on a contact we cannot mint a link
                // for should get an empty string rather than a literal token in
                // the email.
                runtimeReplacements[PlaceholderEngine.UnsubscribeUrlKey] = "";

                // {subject_instructions}. The subject is written in the same
                // call as the body now, so the blueprint needs a slot to drop
                // the subject rules into. Registered empty for the same reason
                // as the keys above, and filled further down once the campaign
                // values it may itself contain can be resolved.
                runtimeReplacements[SubjectInstructionsKey] = "";

                foreach (var kv in customFields)
                    runtimeReplacements[kv.Key] = kv.Value ?? "";

                // runtime keys must SURVIVE the campaign-level pass
                var campaignOnlyValues = campaignPlaceholderValues
                    .Where(kv => !runtimeReplacements.ContainsKey(kv.Key))
                    .ToDictionary(kv => kv.Key, kv => CleanPlaceholderValue(kv.Key, kv.Value), StringComparer.OrdinalIgnoreCase);

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

                // ---- LinkedIn messages already sent to this contact ----
                // {linkedin_messages} is the slot; use_linkedin_message is the
                // yes/no switch, read the same way as use_email_history. Opt-in:
                // resolved only when the blueprint contains the token, and never
                // appended as a context section — an email blueprint that
                // doesn't ask for it generates exactly as before.
                var hasLinkedInHistoryPlaceholder =
                    ContainsPlaceholder(campaignBlueprint, PlaceholderEngine.LinkedInHistoryKey);

                var linkedInHistoryEnabled = PlaceholderEngine.IsHistoryEnabled(
                    campaignPlaceholderValues, PlaceholderEngine.LinkedInHistoryToggleKey);

                var linkedInHistory = hasLinkedInHistoryPlaceholder && linkedInHistoryEnabled
                    ? await _promptContext.GetSentLinkedInContextAsync(parsedClientId, contact.id)
                    : new LinkedInSentContext();

                runtimeReplacements[PlaceholderEngine.LinkedInHistoryKey] = linkedInHistory.Text;

                // ---- the LinkedIn chat, both sides ----
                // {linkedin_conversation} lets an email blueprint answer what
                // the contact said on LinkedIn: "they replied there, follow up
                // by email". Free on this channel because the prompt context
                // service is shared - nothing here is LinkedIn-specific.
                var hasLinkedInConversationPlaceholder =
                    ContainsPlaceholder(campaignBlueprint, PlaceholderEngine.LinkedInConversationKey);

                var linkedInConversationEnabled = PlaceholderEngine.IsHistoryEnabled(
                    campaignPlaceholderValues, PlaceholderEngine.LinkedInConversationToggleKey);

                var linkedInConversation = hasLinkedInConversationPlaceholder && linkedInConversationEnabled
                    ? await _promptContext.GetLinkedInConversationAsync(parsedClientId, contact.id)
                    : new LinkedInConversationContext();

                runtimeReplacements[PlaceholderEngine.LinkedInConversationKey] = linkedInConversation.Text;

                // ---- the contact's unsubscribe link ----
                // {unsubscribe_url} is the slot. Minting a link writes a token
                // row, so it is resolved only when the blueprint actually asks
                // for one — a blueprint without the token behaves exactly as
                // before and costs nothing extra.
                var hasUnsubscribePlaceholder =
                    ContainsPlaceholder(campaignBlueprint, PlaceholderEngine.UnsubscribeUrlKey);

                var unsubscribeUrl = hasUnsubscribePlaceholder
                    ? await BuildUnsubscribeUrlAsync(parsedClientId, contact)
                    : "";

                runtimeReplacements[PlaceholderEngine.UnsubscribeUrlKey] = unsubscribeUrl;

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
                // The email-generation model is set application-wide by an admin
                // (Settings > AI models); the blueprint's own model is only a
                // fallback for installs that haven't configured one.
                var selectedModel = await _aiModelSettings.GetModelAsync(AiModelPurposes.EmailGeneration);

                if (string.IsNullOrWhiteSpace(selectedModel))
                {
                    selectedModel = !string.IsNullOrWhiteSpace(template.SelectedModel)
                        ? template.SelectedModel
                        : (!string.IsNullOrWhiteSpace(template.TemplateDefinition.SelectedModel)
                            ? template.TemplateDefinition.SelectedModel
                            : AiModelDefaults.EmailGenerationModel);
                }

                var isGptModel = selectedModel.Trim().StartsWith("gpt", StringComparison.OrdinalIgnoreCase);

                // ---- subject instructions, folded into the body call ----
                // Subject and body used to be two calls: write the email, then
                // hand the finished email back and ask for a line to put on
                // top. One call does both, which halves the latency and the
                // per-contact spend on the writing model.
                //
                // The exception is an instruction that asks to read the
                // finished email ({generated_pitch}). That cannot be answered
                // before the email exists, so those blueprints keep the second
                // call — see the subject resolution below.
                var aiMode = campaignPlaceholderValues.TryGetValue("email_subject-AI", out var aiModeValue)
                    ? (aiModeValue ?? "").Trim().ToLower()
                    : "yes";

                var isAiSubject = aiMode != "no";

                var manualSubjectTemplate = campaignPlaceholderValues.TryGetValue("email_subject-manual", out var manualVal)
                    ? manualVal ?? ""
                    : "";

                // The definition table is the live source of truth for subject
                // instructions — CampaignTemplates.SubjectInstructions is only a
                // snapshot taken when the campaign was created, so an admin edit
                // to the definition would never reach existing campaigns.
                // The campaign copy is kept as a fallback for definitions that
                // have no instruction of their own.
                var subjectInstructionTemplate =
                    !string.IsNullOrWhiteSpace(template.TemplateDefinition.SubjectInstructions)
                        ? template.TemplateDefinition.SubjectInstructions
                        : template.SubjectInstructions ?? "";

                var subjectInstructionSource =
                    !string.IsNullOrWhiteSpace(template.TemplateDefinition.SubjectInstructions)
                        ? "template-definition"
                        : (!string.IsNullOrWhiteSpace(template.SubjectInstructions) ? "campaign-template" : "none");

                var subjectNeedsFinishedBody =
                    ContainsPlaceholder(subjectInstructionTemplate, "generated_pitch");

                var subjectIsFolded =
                    isAiSubject &&
                    !subjectNeedsFinishedBody &&
                    !string.IsNullOrWhiteSpace(subjectInstructionTemplate);

                if (subjectIsFolded)
                {
                    // Same two-pass fill the body gets: campaign-level values
                    // first, then the per-contact runtime values, so a campaign
                    // value that itself contains {first_name} still resolves.
                    var filledForFold = ApplyPlaceholders(
                        ApplyPlaceholders(subjectInstructionTemplate, campaignOnlyValues),
                        runtimeReplacements);

                    // Subject rules are authored in a rich-text field, so they
                    // arrive as HTML. As a system message that cost nothing;
                    // inside the prompt the markup burns tokens and buries the
                    // instruction, so it is stripped the same way campaign
                    // placeholder values are.
                    runtimeReplacements[SubjectInstructionsKey] =
                        PromptTextCleaner.LooksLikeHtml(filledForFold)
                            ? PromptTextCleaner.StripHtml(filledForFold)
                            : filledForFold;
                }

                // ---- body prompt ----
                var finalPrompt = ApplyPlaceholders(campaignBlueprint, runtimeReplacements);

                // A blueprint that has no {subject_instructions} slot still has
                // to receive the rules, or folding would silently drop them and
                // the model would invent a subject from nothing.
                var hasSubjectInstructionsPlaceholder =
                    ContainsPlaceholder(campaignBlueprint, SubjectInstructionsKey);

                if (subjectIsFolded && !hasSubjectInstructionsPlaceholder)
                {
                    finalPrompt = AppendContextSection(
                        finalPrompt,
                        "Subject line rules (write the subject for this email and return it in the \"subject\" field):",
                        runtimeReplacements[SubjectInstructionsKey]);
                }

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

                // ---- web / personalization search (every model) ----
                PitchResult? searchResult = null;
                string webSearchData = "";
                string filledSearchInstructions = "";

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
                        searchResult = await GenerateWebSearchByProviderAsync(new EnquiryRequest
                        {
                            Prompt = filledSearchInstructions,
                            ScrappedData = "",
                            ModelName = await _aiModelSettings.GetModelAsync(AiModelPurposes.WebSearch)
                        }, parsedClientId);

                        if (searchResult != null && searchResult.IsSuccess)
                            webSearchData = searchResult.Content ?? "";

                        finalPrompt = finalPrompt.Contains("{web_searched_data}")
                            ? finalPrompt.Replace("{web_searched_data}", webSearchData)
                            : $"{finalPrompt}\n\n{webSearchData}";
                    }
                }

                runtimeReplacements["search_output_summary"] = webSearchData;

                // ---- system prompt is EMPTY (matches frontend) ----
                var systemPrompt = "";

                // From here on the prompt is frozen. Everything the response
                // reports as the final prompt reads this variable, so what the
                // UI shows is byte-for-byte what the model received.
                var promptSentToAi = finalPrompt;

                // The dedicated search above already supplied the research, so
                // the writing call must not search again on any provider.
                var bodyResult = await GeneratePitchByProviderAsync(new EnquiryRequest
                {
                    Prompt = promptSentToAi,
                    ScrappedData = systemPrompt,
                    ModelName = selectedModel,
                    DisableWebSearchTool = true
                });

                if (!bodyResult.IsSuccess || string.IsNullOrWhiteSpace(bodyResult.Content))
                {
                    return StatusCode(500, new
                    {
                        Message = "Failed to generate email body",
                        Error = bodyResult.Content,
                        FinalPrompt = promptSentToAi,
                        WebSearchData = webSearchData,
                        Notes = generationNotes,
                        Emails = emailConversation,
                        ProfessionalSummary = professionalSummary
                    });
                }

                // ---- read the reply ----
                // The generator answers with one JSON object: the subject, the
                // body as clean HTML, and the source highlights as records.
                // A reply that does not parse is taken as bare HTML, which is
                // what an un-migrated blueprint still returns — those keep the
                // old behaviour, highlight spans baked into the body and all.
                var generated = GeneratedEmailParser.Parse(bodyResult.Content);

                // A reply that tried to be the JSON object and did not finish
                // is not an email. Saving it would hand the contact a body
                // full of visible JSON and charge a credit for it, so this
                // fails the same way an empty generation does.
                if (generated.LooksTruncated)
                {
                    Log.Error(
                        "Email generation returned unreadable JSON — most likely truncated by the model's "
                        + "output budget. ContactId={ContactId}, BlueprintId={BlueprintId}, ReplyLength={Length}",
                        request.ContactId, request.BlueprintId, (bodyResult.Content ?? "").Length);

                    return StatusCode(500, new
                    {
                        Message = "The generated email came back as unreadable JSON, which usually means the "
                                + "model's output limit was reached. Raise MaxTokens for this model in Model "
                                + "rates, or shorten the blueprint, then kraft again.",
                        Error = bodyResult.Content,
                        FinalPrompt = promptSentToAi,
                        WebSearchData = webSearchData,
                        Notes = generationNotes,
                        Emails = emailConversation,
                        ProfessionalSummary = professionalSummary
                    });
                }

                if (!generated.IsStructured)
                {
                    Log.Information(
                        "Email generation reply was not structured JSON; falling back to raw HTML. "
                        + "ContactId={ContactId}, BlueprintId={BlueprintId}",
                        request.ContactId, request.BlueprintId);
                }

                var emailBody = generated.BodyHtml;

                // ---- subject ----
                string subjectLine = "";
                PitchResult? subjectResult = null;
                string filledSubjectInstruction = subjectIsFolded
                    ? runtimeReplacements[SubjectInstructionsKey]
                    : "";

                var subjectReplacements = new Dictionary<string, string>(runtimeReplacements, StringComparer.OrdinalIgnoreCase)
                {
                    ["generated_pitch"] = emailBody
                };

                // Subjects get the same two-pass fill as the body: campaign-level
                // values first, then per-contact runtime values. Order matters —
                // campaignOnlyValues already excludes runtime keys, and running
                // the runtime pass second means a campaign value that itself
                // contains {first_name} still resolves.
                string FillSubjectPlaceholders(string text) =>
                    ApplyPlaceholders(ApplyPlaceholders(text, campaignOnlyValues), subjectReplacements);

                string subjectMode;

                if (!isAiSubject)
                {
                    subjectMode = "manual";
                    if (!string.IsNullOrWhiteSpace(manualSubjectTemplate))
                        subjectLine = FillSubjectPlaceholders(manualSubjectTemplate);
                }
                else if (subjectIsFolded && !string.IsNullOrWhiteSpace(generated.Subject))
                {
                    // The point of the whole change: the subject came back with
                    // the body, so there is no second call to make.
                    subjectMode = "ai-folded";
                    subjectLine = generated.Subject.Trim();
                }
                else
                {
                    // Either the blueprint's subject rules need the finished
                    // email, or folding was asked for and the model did not
                    // return a subject. Both land on the original second call,
                    // so a contact never ends up with a blank subject.
                    subjectMode = subjectIsFolded ? "ai-second-call-fallback" : "ai-second-call";

                    filledSubjectInstruction = FillSubjectPlaceholders(subjectInstructionTemplate);

                    subjectResult = await GeneratePitchByProviderAsync(new EnquiryRequest
                    {
                        Prompt = emailBody,
                        ScrappedData = filledSubjectInstruction,
                        ModelName = selectedModel,
                        // A subject is written from the body that is already in
                        // hand, so there is nothing left to research. Without
                        // this the Responses call still carries the web-search
                        // tool and can spend a live search on one line of text.
                        DisableWebSearchTool = true
                    });

                    if (subjectResult.IsSuccess)
                        subjectLine = subjectResult.Content ?? "";
                }

                var highlightsJson = GeneratedEmailParser.Serialize(generated.Highlights);

                // ---- Preview mode: no DB write, no credit, no history ----
                if (!request.Preview)
                {
                    contact.email_body = emailBody;
                    contact.email_subject = subjectLine;

                    // Always written, including the null that clears it. A
                    // re-kraft that produced no highlights must not leave the
                    // previous generation's highlights pointing at wording
                    // that is no longer in the email.
                    contact.email_highlights = highlightsJson;

                    // Persist the research the same way the Generate-insights
                    // endpoint does, so the profile Insights panel always shows
                    // the latest data. Only overwrite on a successful search —
                    // a skipped or failed one must not wipe what's stored.
                    if (!string.IsNullOrWhiteSpace(webSearchData))
                        contact.web_search_data = webSearchData;

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
                    EmailBody = emailBody,

                    // Source highlights, kept out of the body. The UI matches
                    // each Text against the rendered email to paint it.
                    EmailHighlights = generated.Highlights,

                    // 👇 EVERYTHING THE UI SHOWS IN THE INSIGHTS TABS
                    WebSearchData = webSearchData,
                    FinalPrompt = promptSentToAi,
                    Notes = generationNotes,
                    Emails = emailConversation,
                    ProfessionalSummary = professionalSummary,
                    EmailCount = insights.EmailCount,
                    LinkedInMessages = linkedInHistory.Text,
                    LinkedInMessageCount = linkedInHistory.Count,
                    LinkedInMessagesSentTotal = linkedInHistory.TotalSent,
                    LinkedInConversation = linkedInConversation.Text,
                    LinkedInConversationCount = linkedInConversation.Count,
                    LinkedInConversationReplies = linkedInConversation.InboundCount,

                    // Which inputs actually reached the model. These are not
                    // "we intended to add it" flags — each one is verified
                    // against the prompt string that was sent.
                    UsedInGeneration = new
                    {
                        Notes = notesUsed && PromptContains(promptSentToAi, generationNotes),
                        Emails = emailsUsed && PromptContains(promptSentToAi, emailConversation),
                        ProfessionalSummary = summaryUsed && PromptContains(promptSentToAi, professionalSummary),
                        WebSearch = !string.IsNullOrWhiteSpace(webSearchData)
                                    && PromptContains(promptSentToAi, webSearchData),
                        LinkedInMessages = PromptContains(promptSentToAi, linkedInHistory.Text),
                        LinkedInConversation = PromptContains(promptSentToAi, linkedInConversation.Text)
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
                        LinkedInHistoryPlaceholderFound = hasLinkedInHistoryPlaceholder,
                        LinkedInHistoryEnabled = linkedInHistoryEnabled,
                        LinkedInConversationPlaceholderFound = hasLinkedInConversationPlaceholder,
                        LinkedInConversationEnabled = linkedInConversationEnabled,
                        UnsubscribePlaceholderFound = hasUnsubscribePlaceholder,
                        UnsubscribeUrl = unsubscribeUrl,
                        FilledSearchInstructions = filledSearchInstructions,
                        SubjectMode = subjectMode,
                        SubjectInstructionSource = subjectInstructionSource,
                        SubjectFolded = subjectIsFolded,
                        SubjectInstructionsPlaceholderFound = hasSubjectInstructionsPlaceholder,

                        // Did the model answer with the JSON contract, and did
                        // every highlight it returned actually land in the
                        // body? Both are the signals to watch after a prompt
                        // edit — a drop to false/non-zero means the blueprint
                        // and the parser have drifted apart.
                        StructuredReply = generated.IsStructured,
                        HighlightCount = generated.Highlights.Count,
                        UnmatchedHighlightCount = generated.UnmatchedHighlights,
                        FilledSubjectInstruction = filledSubjectInstruction,
                        ManualSubjectTemplate = manualSubjectTemplate,
                        RuntimeReplacements = runtimeReplacements,
                        CampaignPlaceholderValues = campaignPlaceholderValues,

                        // Enough to answer "did the past emails make it into the
                        // prompt?" without eyeballing a 20k-character string.
                        PromptDiagnostics = new
                        {
                            PromptLength = promptSentToAi.Length,
                            EmailContextLength = emailConversation.Length,
                            EmailMessageCount = insights.EmailCount,
                            EmailsInPrompt = PromptContains(promptSentToAi, emailConversation),
                            EmailsInjectedVia = !emailHistoryEnabled
                                ? "disabled"
                                : string.IsNullOrWhiteSpace(emailConversation)
                                    ? "no-email-history-found"
                                    : hasEmailPlaceholder ? "placeholder" : "appended-section",
                            UnresolvedPlaceholders = FindUnresolvedPlaceholders(promptSentToAi)
                        }
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
                    EmailHighlights = ReadStoredHighlights(contact.email_highlights),

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
        // Unsubscribe link
        // ============================================

        /// <summary>
        /// Mints the contact's unsubscribe link for {unsubscribe_url}. The link
        /// is branded with the SENDING client's company — the same name the
        /// one-click header uses — not the prospect's, because the unsubscribe
        /// page is that client's.
        ///
        /// A failure here must not fail the generation: an email that comes back
        /// without its link is a visible, fixable problem, whereas losing the
        /// whole kraft over it costs the contact a credit for nothing.
        /// </summary>
        private async Task<string> BuildUnsubscribeUrlAsync(int clientId, Contact contact)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(contact.email))
                {
                    Log.Warning(
                        "Blueprint asked for {{unsubscribe_url}} but the contact has no email address. "
                        + "ClientId={ClientId}, ContactId={ContactId}",
                        clientId, contact.id);
                    return "";
                }

                var companyName = await _dbContext.ClientDetails
                    .AsNoTracking()
                    .Where(c => c.Id == clientId)
                    .Select(c => c.CompanyName)
                    .FirstOrDefaultAsync();

                return await _unsubscribeRepository.GenerateUnsubscribeLinkAsync(
                    string.IsNullOrWhiteSpace(companyName) ? "Pitchkraft" : companyName,
                    clientId,
                    contact.id,
                    contact.email);
            }
            catch (Exception ex)
            {
                Log.Error(ex,
                    "Failed to generate unsubscribe link. ClientId={ClientId}, ContactId={ContactId}",
                    clientId, contact.id);
                return "";
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
            // Sequential, not Task.WhenAll: both repositories are handed the same
            // scoped AppDbContext, and EF Core allows only one operation on a
            // context at a time. Run in parallel and the loser throws "a second
            // operation was started on this context instance", which the catch
            // blocks below turn into an empty result — the email history then
            // silently vanishes from the prompt.
            var notes = await GetGenerationNotesAsync(clientId, contactId);
            var emailContext = await GetEmailConversationContextAsync(clientId, contactId);

            return new ContactInsights
            {
                Notes = notes,
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

        // Campaign placeholder values are authored in rich-text fields, so they
        // arrive as HTML. The model only needs the words — sending the markup
        // burns tokens and buries the instruction. The example output email is
        // the exception: its HTML is kept so the model reproduces the formatting
        // (paragraphs, bold, lists, links); only non-content markup is dropped.
        private static readonly HashSet<string> ExampleOutputKeys =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "example_output_email",
                "example_output"
            };

        private static string CleanPlaceholderValue(string key, string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return value ?? "";

            if (ExampleOutputKeys.Contains(key))
                return PromptTextCleaner.CleanEmailHtml(value);

            return PromptTextCleaner.LooksLikeHtml(value)
                ? PromptTextCleaner.StripHtml(value)
                : value;
        }

        private static string AppendContextSection(string prompt, string label, string content)
            => string.IsNullOrWhiteSpace(content)
                ? prompt
                : $"{prompt}\n\n{label}\n{content.Trim()}";

        // Did a resolved input really land in the prompt? Compared on a slice
        // rather than the whole value, because placeholder substitution trims
        // and re-wraps what it inserts.
        private static bool PromptContains(string prompt, string? value)
        {
            var probe = (value ?? "").Trim();

            if (probe.Length == 0)
                return false;

            if (probe.Length > 120)
                probe = probe[..120];

            return prompt.Contains(probe, StringComparison.Ordinal);
        }

        // Placeholders the blueprint asked for that nothing filled in. A literal
        // {something} reaching the model is always a bug, so surface it.
        private static readonly Regex PlaceholderPattern =
            new(@"\{([a-z0-9_\-]{2,60})\}", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static List<string> FindUnresolvedPlaceholders(string prompt)
            => PlaceholderPattern.Matches(prompt)
                .Select(m => m.Groups[1].Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        // Strips markup but keeps paragraph breaks, so multi-line LinkedIn
        // summaries and notes stay readable in the prompt and in the UI.
        private static string StripHtml(string? input)
            => PromptTextCleaner.StripHtml(input);

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

                    var body = PromptTextCleaner.CleanEmailBody(ReadStringProperty(email, "body"));
                    if (!string.IsNullOrWhiteSpace(body))
                        builder.Append($"\nEmail Body:\n{body}");
                }

                return new EmailContextResult
                {
                    Text = builder.ToString().Trim(),
                    Count = emailCount
                };
            }
            catch (Exception ex)
            {
                // Swallowing this quietly makes a failure look identical to
                // "this contact has no past emails", so it gets logged.
                Log.Error(ex,
                    "Failed to build email conversation context. ClientId={ClientId}, ContactId={ContactId}",
                    clientId, contactId);
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


        /// <summary>
        /// The date the model should cite for a note: when it was last changed,
        /// which is its update if it has one and its creation otherwise.
        /// </summary>
        private static string DescribeNoteDate(JsonElement note)
        {
            var updated = ReadDateProperty(note, "updatedAt");
            var stamp = updated ?? ReadDateProperty(note, "createdAt");

            if (stamp == null)
                return "";

            var label = updated.HasValue ? "updated" : "written";
            var date = stamp.Value.ToString("dddd, MMMM d, yyyy", CultureInfo.InvariantCulture);

            return $"{label} {date}";
        }

        private static DateTime? ReadDateProperty(JsonElement element, string property)
        {
            if (element.ValueKind != JsonValueKind.Object)
                return null;

            if (!element.TryGetProperty(property, out var prop))
                return null;

            if (prop.ValueKind == JsonValueKind.String && prop.TryGetDateTime(out var value))
                return value;

            return null;
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

                    if (string.IsNullOrWhiteSpace(cleaned))
                        continue;

                    // Numbered and dated, so the model can say which note a line
                    // came from and how old that note is. A note with neither
                    // date keeps its heading rather than being dropped.
                    var heading = $"Note {usableNotes.Count + 1}";
                    var written = DescribeNoteDate(item);

                    usableNotes.Add(string.IsNullOrEmpty(written)
                        ? $"{heading}:\n{cleaned}"
                        : $"{heading} ({written}):\n{cleaned}");
                }

                // A blank line between notes: joined by a single newline they
                // read as one block, and nothing in the email could then be
                // attributed to one note rather than another.
                return string.Join("\n\n", usableNotes);
            }
            catch (Exception ex)
            {
                Log.Error(ex,
                    "Failed to build generation notes. ClientId={ClientId}, ContactId={ContactId}",
                    clientId, contactId);
                return "";
            }
        }

        /// <summary>
        /// The contact's stored highlights, as records rather than a JSON
        /// string, so the UI reads them the same way whether they came from a
        /// fresh generation or from the database. A row written before the
        /// split, or one holding unreadable JSON, yields an empty list — the
        /// email still renders, just without highlights.
        /// </summary>
        private static List<EmailHighlight> ReadStoredHighlights(string? stored)
        {
            if (string.IsNullOrWhiteSpace(stored))
                return new List<EmailHighlight>();

            try
            {
                return JsonSerializer.Deserialize<List<EmailHighlight>>(
                    stored,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? new List<EmailHighlight>();
            }
            catch (JsonException ex)
            {
                Log.Warning(ex, "Stored email highlights could not be read; returning none.");
                return new List<EmailHighlight>();
            }
        }

        private static bool IsDeepSeekModel(string? modelName)
            => modelName?.StartsWith("deepseek-", StringComparison.OrdinalIgnoreCase) == true;

        private static bool IsQwenModel(string? modelName)
            => modelName?.StartsWith("qwen", StringComparison.OrdinalIgnoreCase) == true;

        private Task<PitchResult> GeneratePitchByProviderAsync(EnquiryRequest request)
            => IsDeepSeekModel(request.ModelName)
                ? _deepSeekService.GeneratePitchAsync(request)
                : IsQwenModel(request.ModelName)
                    ? _qwenService.GeneratePitchAsync(request)
                    : _pitchService.GeneratePitchAsync(request);

        private Task<PitchResult> GenerateWebSearchByProviderAsync(EnquiryRequest request, int clientId)
            => IsDeepSeekModel(request.ModelName)
                ? _deepSeekService.GenerateWebSearchAsync(request, clientId)
                : IsQwenModel(request.ModelName)
                    ? _qwenService.GenerateWebSearchAsync(request, clientId)
                    : _pitchService.GenerateWebSearchAsync(request, clientId);
    }
}
