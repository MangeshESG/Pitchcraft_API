namespace PitchGenApi.Services
{
    using System.Diagnostics;
    using System.Text;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Options;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;
    using PitchGenApi.Database;
    using PitchGenApi.Interfaces;
    using PitchGenApi.Model;
    using PitchGenApi.Model.DTOs;
    using PitchGenApi.Models;

    /// <summary>
    /// Runs the four Audience Assurance checks over selected contacts.
    ///
    /// The economics drive most of the design here. Tokens are almost free —
    /// a hundred contacts is a fraction of a cent — while each web search
    /// costs roughly a cent, so everything is arranged to send one request per
    /// batch instead of one per contact, to reuse company research across
    /// contacts at the same employer, and to keep the checks that need no
    /// search from ever making one. Every run records what it actually spent.
    /// </summary>
    public class ContactValidationService : IContactValidationService
    {
        /// <summary>
        /// Contacts per model request. The spec's own guidance is 50-100; 50
        /// keeps the returned JSON array comfortably inside max_output_tokens,
        /// since every contact costs output tokens for its comments.
        /// </summary>
        private const int DefaultBatchSize = 50;

        /// <summary>Contacts covered by one credit.</summary>
        private const int ContactsPerCredit = 10;

        /// <summary>
        /// How long a cached company classification is trusted. Companies get
        /// acquired and rebranded, so a stale row is re-researched rather than
        /// believed forever.
        /// </summary>
        private static readonly TimeSpan CompanyIntelligenceMaxAge = TimeSpan.FromDays(90);

        private readonly AppDbContext _context;
        private readonly ContactRepository _contactRepository;
        private readonly IAiModelSettingsService _aiModelSettings;
        private readonly IPromptSettingsService _promptSettings;
        private readonly IProspeoEmailService _prospeoService;
        private readonly IHunterEmailService _hunterService;
        private readonly DeepSeekPitchService _deepSeekService;
        private readonly HttpClient _httpClient;
        private readonly IConfiguration _configuration;
        private readonly ILogger<ContactValidationService> _logger;
        private readonly string _openAiApiKey;

        public ContactValidationService(
            AppDbContext context,
            ContactRepository contactRepository,
            IAiModelSettingsService aiModelSettings,
            IPromptSettingsService promptSettings,
            IProspeoEmailService prospeoService,
            IHunterEmailService hunterService,
            DeepSeekPitchService deepSeekService,
            HttpClient httpClient,
            IConfiguration configuration,
            IOptions<OpenAISettings> openAiOptions,
            ILogger<ContactValidationService> logger)
        {
            _context = context;
            _contactRepository = contactRepository;
            _aiModelSettings = aiModelSettings;
            _promptSettings = promptSettings;
            _prospeoService = prospeoService;
            _hunterService = hunterService;
            _deepSeekService = deepSeekService;
            _httpClient = httpClient;
            _configuration = configuration;
            _logger = logger;
            _openAiApiKey = openAiOptions.Value.ApiKey;
        }

        private int BatchSize
        {
            get
            {
                var configured = _configuration.GetValue<int?>("Validation:BatchSize");
                return configured is > 0 and <= 200 ? configured.Value : DefaultBatchSize;
            }
        }

        /// <summary>
        /// What one server-side web search costs, in dollars. Not returned by
        /// the providers, so it has to be configured; without it a run's
        /// reported cost would only ever show the near-zero token half.
        /// </summary>
        private decimal WebSearchCostPerCall =>
            _configuration.GetValue<decimal?>("Validation:WebSearchCostPerCall") ?? 0.01m;

        // =================================================================
        // Queueing
        // =================================================================

        public async Task<ValidationJobDto> QueueAsync(RunValidationRequestDto request)
        {
            if (request == null || request.ClientId <= 0)
                throw new InvalidOperationException("A valid client is required.");

            if (!ValidationCheckTypes.IsKnown(request.CheckType))
                throw new InvalidOperationException($"'{request.CheckType}' is not a validation check.");

            var checkType = ValidationCheckTypes.Normalize(request.CheckType);

            var contactIds = (request.ContactIds ?? new List<int>())
                .Where(id => id > 0)
                .Distinct()
                .ToList();

            if (contactIds.Count == 0)
                throw new InvalidOperationException("Select at least one contact to validate.");

            // Only this client's contacts, whatever the caller sent.
            var ownedIds = await OwnedContactIdsAsync(request.ClientId, contactIds);

            if (ownedIds.Count == 0)
                throw new InvalidOperationException("None of the selected contacts belong to this client.");

            int? briefId = null;

            if (ValidationCheckTypes.RequiresBrief(checkType))
            {
                briefId = request.BriefId;

                if (briefId is null or <= 0)
                {
                    // Fall back to the client's default brief, so a run started
                    // from a context with no picker still has something to score
                    // against rather than failing at the model.
                    briefId = await _context.contact_fit_briefs
                        .Where(b => b.ClientId == request.ClientId && b.IsDefault)
                        .Select(b => (int?)b.Id)
                        .FirstOrDefaultAsync();
                }

                if (briefId is null)
                    throw new InvalidOperationException(
                        "Contact fit needs a targeting brief. Pick one, or save a default in Settings > Verification.");

                var briefExists = await _context.contact_fit_briefs
                    .AnyAsync(b => b.Id == briefId && b.ClientId == request.ClientId);

                if (!briefExists)
                    throw new InvalidOperationException("That brief no longer exists.");
            }

            string? modelName = null;

            if (ValidationCheckTypes.UsesModel(checkType))
            {
                // Fail here rather than at the model: an unconfigured prompt
                // would otherwise spend the client's credits producing nothing.
                var prompt = await _promptSettings.GetPromptAsync(checkType);

                if (string.IsNullOrWhiteSpace(prompt))
                {
                    var (label, _) = ValidationCheckTypes.Describe(checkType);
                    throw new InvalidOperationException(
                        $"The {label} instruction has not been configured yet. An admin can add it under Settings > Admin > Prompts.");
                }

                modelName = await _aiModelSettings.GetModelAsync(checkType);
            }

            var credits = CreditsFor(ownedIds.Count);

            if (!await _contactRepository.CreditDeduction(request.ClientId, credits))
            {
                throw new InvalidOperationException(
                    $"This run needs {credits} credit{(credits == 1 ? "" : "s")} " +
                    $"for {ownedIds.Count} contact{(ownedIds.Count == 1 ? "" : "s")}, " +
                    "and the balance will not cover it.");
            }

            var job = new ContactValidationJob
            {
                ClientId = request.ClientId,
                CheckType = checkType,
                BriefId = briefId,
                ModelName = modelName,
                Provider = ProviderFor(checkType, modelName),
                Status = ValidationJobStatuses.Queued,
                ContactCount = ownedIds.Count,
                CreditsCharged = credits,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = request.RequestedBy
            };

            _context.contact_validation_jobs.Add(job);
            await _context.SaveChangesAsync();

            _context.contact_validation_job_items.AddRange(
                ownedIds.Select(id => new ContactValidationJobItem
                {
                    JobId = job.Id,
                    ContactId = id,
                    Status = ValidationItemStatuses.Pending
                }));

            await _context.SaveChangesAsync();

            return ToDto(job);
        }

        /// <summary>One credit per ten contacts, rounded up — ten or fewer still costs one.</summary>
        private static int CreditsFor(int contactCount) =>
            (int)Math.Ceiling(contactCount / (double)ContactsPerCredit);

        private static string ProviderFor(string checkType, string? modelName)
        {
            if (checkType == ValidationCheckTypes.EmailVerification)
                return "prospeo";

            return LooksLikeDeepSeek(modelName) ? "deepseek" : "openai";
        }

        private static bool LooksLikeDeepSeek(string? modelName) =>
            modelName?.StartsWith("deepseek", StringComparison.OrdinalIgnoreCase) == true;

        private async Task<List<int>> OwnedContactIdsAsync(int clientId, List<int> contactIds)
        {
            var dataFileIds = await _context.data_files
                .AsNoTracking()
                .Where(df => df.client_id == clientId)
                .Select(df => df.id)
                .ToListAsync();

            if (dataFileIds.Count == 0)
                return new List<int>();

            return await _context.contacts
                .AsNoTracking()
                .Where(c => contactIds.Contains(c.id) &&
                            c.DataFileId.HasValue &&
                            dataFileIds.Contains(c.DataFileId.Value))
                .Select(c => c.id)
                .ToListAsync();
        }

        // =================================================================
        // Running
        // =================================================================

        public async Task ProcessJobAsync(int jobId, CancellationToken cancellationToken = default)
        {
            var job = await _context.contact_validation_jobs
                .FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken);

            if (job == null)
                return;

            var timer = Stopwatch.StartNew();

            job.Status = ValidationJobStatuses.Running;
            job.StartedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);

            try
            {
                var items = await _context.contact_validation_job_items
                    .Where(i => i.JobId == jobId)
                    .ToListAsync(cancellationToken);

                var contactIds = items.Select(i => i.ContactId).ToList();

                var contacts = await _context.contacts
                    .AsNoTracking()
                    .Where(c => contactIds.Contains(c.id))
                    .ToListAsync(cancellationToken);

                if (job.CheckType == ValidationCheckTypes.EmailVerification)
                {
                    await RunEmailVerificationAsync(job, items, contacts, cancellationToken);
                }
                else
                {
                    await RunModelCheckAsync(job, items, contacts, cancellationToken);
                }

                job.FailedCount = items.Count(i => i.Status == ValidationItemStatuses.Failed);
                job.ProcessedCount = items.Count(i => i.Status == ValidationItemStatuses.Completed);

                job.Status = job.FailedCount == 0
                    ? ValidationJobStatuses.Completed
                    : job.ProcessedCount == 0
                        ? ValidationJobStatuses.Failed
                        : ValidationJobStatuses.Partial;
            }
            catch (Exception ex)
            {
                // A run that dies mid-way keeps whatever it wrote; the job
                // records why the rest is missing rather than vanishing. The
                // contacts it did get through still count, which is what the
                // refund below is computed from.
                job.Status = ValidationJobStatuses.Failed;
                job.ErrorMessage = ex.Message;
                _logger.LogError(ex, "Validation job {JobId} failed.", jobId);
            }
            finally
            {
                timer.Stop();
                job.ElapsedMs = (int)timer.ElapsedMilliseconds;
                job.CompletedAt = DateTime.UtcNow;

                // In the finally so a crashed run refunds too. Charging for
                // fifty contacts after processing ten would be the worst
                // possible failure mode of a paid feature.
                await RefundUnearnedCreditsAsync(job, cancellationToken);

                try
                {
                    await _context.SaveChangesAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Could not save the final state of validation job {JobId}.", jobId);
                }
            }
        }

        /// <summary>
        /// Returns whole credits the run did not earn. Charged per ten
        /// contacts, so a job that processed 48 of 50 still owes all five —
        /// only a shortfall that crosses a ten-contact boundary is refundable.
        /// </summary>
        private async Task RefundUnearnedCreditsAsync(
            ContactValidationJob job,
            CancellationToken cancellationToken)
        {
            var earned = CreditsFor(job.ProcessedCount);

            if (earned >= job.CreditsCharged)
                return;

            var refund = job.CreditsCharged - earned;

            try
            {
                if (await _contactRepository.CreditRefund(job.ClientId, refund))
                {
                    job.CreditsCharged = earned;
                    return;
                }

                _logger.LogWarning(
                    "Validation job {JobId}: {Refund} credit(s) could not be refunded to client {ClientId}.",
                    job.Id, refund, job.ClientId);
            }
            catch (Exception ex)
            {
                // A refund that fails must not lose the job's results as well.
                _logger.LogError(
                    ex, "Validation job {JobId}: the credit refund failed.", job.Id);
            }
        }

        // -----------------------------------------------------------------
        // The three model-backed checks
        // -----------------------------------------------------------------

        private async Task RunModelCheckAsync(
            ContactValidationJob job,
            List<ContactValidationJobItem> items,
            List<Contact> contacts,
            CancellationToken cancellationToken)
        {
            var promptTemplate = await _promptSettings.GetPromptAsync(job.CheckType);

            if (string.IsNullOrWhiteSpace(promptTemplate))
            {
                job.ErrorMessage = "The instruction for this check is no longer configured.";
                items.ForEach(i =>
                {
                    i.Status = ValidationItemStatuses.Failed;
                    i.Error = job.ErrorMessage;
                });
                return;
            }

            var briefText = job.BriefId is int briefId
                ? await _context.contact_fit_briefs
                    .Where(b => b.Id == briefId)
                    .Select(b => b.BriefText)
                    .FirstOrDefaultAsync(cancellationToken) ?? ""
                : "";

            // Duplicates are found here rather than by the model: it is an exact
            // comparison over the whole selection, so it is both cheaper and more
            // reliable done in code, and the model gets told the answer.
            var duplicateFlags = job.CheckType == ValidationCheckTypes.DataIntegrity
                ? DescribeDuplicates(contacts)
                : "";

            var itemsByContact = items.ToDictionary(i => i.ContactId);

            foreach (var batch in contacts.Chunk(BatchSize))
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Only contact fit reuses company research — it is the check
                // whose expensive question is about the employer rather than
                // the person.
                var intelligence = job.CheckType == ValidationCheckTypes.ContactFit
                    ? await LoadCompanyIntelligenceAsync(job.ClientId, batch, cancellationToken)
                    : new Dictionary<string, CompanyIntelligence>();

                var prompt = BuildPrompt(
                    promptTemplate,
                    briefText,
                    duplicateFlags,
                    DescribeCompanyIntelligence(intelligence),
                    BuildContactsJson(batch));

                ModelCallResult call;

                try
                {
                    call = await CallModelAsync(job, prompt, cancellationToken);
                }
                catch (Exception ex)
                {
                    MarkBatchFailed(batch, itemsByContact, "The model call failed: " + ex.Message);
                    continue;
                }

                job.InputTokens += call.InputTokens;
                job.CachedTokens += call.CachedTokens;
                job.OutputTokens += call.OutputTokens;
                job.TotalTokens += call.InputTokens + call.OutputTokens;
                job.WebSearchCalls += call.WebSearchCalls;
                job.CalculatedCost += call.TokenCost + call.WebSearchCalls * WebSearchCostPerCall;

                if (!call.IsSuccess)
                {
                    MarkBatchFailed(batch, itemsByContact, call.Error ?? "The model returned nothing.");
                    continue;
                }

                var parsed = ParseResults(call.Content);

                if (parsed.Count == 0)
                {
                    MarkBatchFailed(batch, itemsByContact,
                        "The model's reply could not be read as JSON results.");
                    continue;
                }

                await ApplyResultsAsync(job, batch, itemsByContact, parsed, intelligence, cancellationToken);

                // Saved per batch so a long run shows progress as it goes, and a
                // crash costs only the batch in flight.
                job.ProcessedCount = items.Count(i => i.Status == ValidationItemStatuses.Completed);
                await _context.SaveChangesAsync(cancellationToken);
            }
        }

        private static void MarkBatchFailed(
            IEnumerable<Contact> batch,
            IReadOnlyDictionary<int, ContactValidationJobItem> itemsByContact,
            string error)
        {
            foreach (var contact in batch)
            {
                if (!itemsByContact.TryGetValue(contact.id, out var item)) continue;

                item.Status = ValidationItemStatuses.Failed;
                item.Error = error;
            }
        }

        private static string BuildPrompt(
            string template,
            string brief,
            string duplicateFlags,
            string companyIntelligence,
            string contactsJson) =>
            template
                .Replace("{brief}", string.IsNullOrWhiteSpace(brief) ? "(no brief supplied)" : brief)
                .Replace("{duplicate_flags}",
                    string.IsNullOrWhiteSpace(duplicateFlags)
                        ? "No duplicates were detected in this batch."
                        : duplicateFlags)
                .Replace("{company_intelligence}",
                    string.IsNullOrWhiteSpace(companyIntelligence)
                        ? "(nothing established yet — research as needed)"
                        : companyIntelligence)
                .Replace("{contacts_json}", contactsJson);

        /// <summary>
        /// The contact fields the checks judge, and nothing else.
        ///
        /// Deliberately compact: short keys, no nulls, and no field the model
        /// is not being asked about. The output is asked to carry only the ID,
        /// a score and comments for the same reason — we already hold the names
        /// and addresses, so paying tokens to have them read back is waste.
        /// </summary>
        private static string BuildContactsJson(IEnumerable<Contact> contacts)
        {
            var rows = contacts.Select(c =>
            {
                var row = new Dictionary<string, object?>
                {
                    ["id"] = c.id.ToString()
                };

                void Add(string key, string? value)
                {
                    if (!string.IsNullOrWhiteSpace(value))
                        row[key] = value.Trim();
                }

                Add("name", c.full_name ?? $"{c.first_name} {c.last_name}".Trim());
                Add("title", c.job_title);
                Add("company", c.company_name);
                Add("website", c.website);
                Add("email", c.email);
                Add("linkedin", c.linkedin_url);
                Add("location", c.country_or_address);

                return row;
            });

            return JsonConvert.SerializeObject(rows, Formatting.None);
        }

        // -----------------------------------------------------------------
        // Duplicates
        // -----------------------------------------------------------------

        /// <summary>
        /// Finds duplicates across the whole selection and describes them for
        /// the prompt.
        ///
        /// Three signals, in the order the spec lists them: the same address,
        /// the same LinkedIn profile, and the same person at the same company
        /// under a slightly different name. All are exact comparisons over
        /// normalised values, which is something code does better and cheaper
        /// than a model — and unlike a model, it sees the entire selection
        /// rather than one batch.
        /// </summary>
        private static string DescribeDuplicates(List<Contact> contacts)
        {
            var lines = new List<string>();

            void Report(string signal, IEnumerable<IGrouping<string, Contact>> groups)
            {
                foreach (var group in groups.Where(g => g.Count() > 1))
                {
                    var ids = string.Join(", ", group.Select(c => c.id).OrderBy(id => id));
                    lines.Add($"- IDs {ids} share the same {signal} ({group.Key}).");
                }
            }

            Report("email address", contacts
                .Where(c => !string.IsNullOrWhiteSpace(c.email))
                .GroupBy(c => c.email!.Trim().ToLowerInvariant()));

            Report("LinkedIn URL", contacts
                .Where(c => !string.IsNullOrWhiteSpace(c.linkedin_url))
                .GroupBy(c => NormaliseLinkedInUrl(c.linkedin_url!)));

            Report("name at the same company", contacts
                .Where(c => !string.IsNullOrWhiteSpace(c.full_name) &&
                            !string.IsNullOrWhiteSpace(c.company_name))
                .GroupBy(c => NormaliseText(c.full_name!) + " @ " + NormaliseText(c.company_name!)));

            return lines.Count == 0
                ? ""
                : "The following duplicates were detected in this selection:\n" +
                  string.Join("\n", lines.Distinct());
        }

        /// <summary>
        /// Reduces a LinkedIn URL to the profile slug, so the same person is
        /// one value whether the row stored a country subdomain, a trailing
        /// slash or a tracking query string.
        /// </summary>
        private static string NormaliseLinkedInUrl(string url)
        {
            var text = url.Trim().ToLowerInvariant();

            var scheme = text.IndexOf("//", StringComparison.Ordinal);
            if (scheme >= 0) text = text[(scheme + 2)..];

            var query = text.IndexOf('?');
            if (query >= 0) text = text[..query];

            var slug = text.LastIndexOf("/in/", StringComparison.Ordinal);
            if (slug >= 0) text = text[(slug + 4)..];

            return text.Trim('/');
        }

        private static string NormaliseText(string value) =>
            new string(value.Trim().ToLowerInvariant()
                .Where(ch => char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch))
                .ToArray())
            .Replace("  ", " ")
            .Trim();

        // -----------------------------------------------------------------
        // Company intelligence
        // -----------------------------------------------------------------

        /// <summary>
        /// Rows already held for the companies in this batch, keyed by the
        /// same value <see cref="CompanyKeyFor"/> produces so a lookup and a
        /// write always agree.
        ///
        /// Loads stale rows as well as fresh ones, and loads them tracked.
        /// A stale row still occupies its (client, domain) slot, so an upsert
        /// has to find it and refresh it in place — adding a second row for
        /// the same company would violate the unique index.
        /// </summary>
        private async Task<Dictionary<string, CompanyIntelligence>> LoadCompanyIntelligenceAsync(
            int clientId,
            IEnumerable<Contact> batch,
            CancellationToken cancellationToken)
        {
            var keys = batch
                .Select(CompanyKeyFor)
                .Where(key => key != null)
                .Select(key => key!)
                .Distinct()
                .ToList();

            if (keys.Count == 0)
                return new Dictionary<string, CompanyIntelligence>();

            var rows = await _context.company_intelligence
                .Where(ci => ci.ClientId == clientId &&
                             keys.Contains(ci.Domain ?? ci.CompanyNameNormalised))
                .ToListAsync(cancellationToken);

            return rows
                .GroupBy(row => row.Domain ?? row.CompanyNameNormalised)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.ResearchedAt).First());
        }

        /// <summary>Whether a cached classification is still worth trusting.</summary>
        private static bool IsFresh(CompanyIntelligence row) =>
            row.ResearchedAt >= DateTime.UtcNow - CompanyIntelligenceMaxAge;

        /// <summary>
        /// The key a company is cached under: its domain where there is one,
        /// its normalised name otherwise. Domain first because two contacts at
        /// the same employer routinely spell the company differently, while
        /// the website rarely varies.
        /// </summary>
        private static string? CompanyKeyFor(Contact contact)
        {
            var domain = ExtractDomain(contact.website) ?? ExtractDomain(contact.email);

            if (domain != null)
                return domain;

            var name = NormaliseText(contact.company_name ?? "");
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }

        /// <summary>
        /// The classifications worth putting in front of the model. Stale rows
        /// are left out so the model researches those companies again — an
        /// acquisition or a rebrand is exactly what an old classification
        /// would get wrong.
        /// </summary>
        private static string DescribeCompanyIntelligence(
            Dictionary<string, CompanyIntelligence> intelligence)
        {
            var usable = intelligence
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Value.Classification) &&
                               IsFresh(pair.Value))
                .ToList();

            if (usable.Count == 0)
                return "";

            var sb = new StringBuilder();

            foreach (var (key, row) in usable)
            {
                sb.AppendLine($"- {key}: {row.Classification!.Trim()}");
            }

            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// Bare registrable domain out of a URL or an email address, or null
        /// when the value points at something that is never an employer.
        /// </summary>
        private static string? ExtractDomain(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            var text = value.Trim().ToLowerInvariant();

            var at = text.LastIndexOf('@');
            if (at >= 0) text = text[(at + 1)..];

            var scheme = text.IndexOf("//", StringComparison.Ordinal);
            if (scheme >= 0) text = text[(scheme + 2)..];

            foreach (var separator in new[] { '/', '?', '#', ':' })
            {
                var index = text.IndexOf(separator);
                if (index >= 0) text = text[..index];
            }

            text = text.Trim().Trim('.');

            if (text.StartsWith("www.", StringComparison.Ordinal))
                text = text[4..];

            var lastDot = text.LastIndexOf('.');

            if (text.Length == 0 || text.Any(char.IsWhiteSpace) ||
                lastDot <= 0 || lastDot >= text.Length - 1)
            {
                return null;
            }

            // A mailbox provider or a social network says nothing about an
            // employer, and caching "gmail.com" as a company would poison every
            // later lookup that shares it.
            return NonCompanyDomains.Any(blocked =>
                text == blocked || text.EndsWith("." + blocked, StringComparison.Ordinal))
                ? null
                : text;
        }

        private static readonly string[] NonCompanyDomains =
        {
            "gmail.com", "googlemail.com", "yahoo.com", "yahoo.co.uk", "hotmail.com",
            "hotmail.co.uk", "outlook.com", "live.com", "msn.com", "aol.com",
            "icloud.com", "me.com", "mac.com", "protonmail.com", "proton.me",
            "gmx.com", "mail.com", "yandex.com", "qq.com", "163.com",
            "linkedin.com", "lnkd.in", "facebook.com", "twitter.com", "x.com",
            "instagram.com", "youtube.com"
        };

        // -----------------------------------------------------------------
        // Model dispatch
        // -----------------------------------------------------------------

        private sealed class ModelCallResult
        {
            public bool IsSuccess { get; init; }
            public string Content { get; init; } = "";
            public string? Error { get; init; }
            public int InputTokens { get; init; }
            public int CachedTokens { get; init; }
            public int OutputTokens { get; init; }
            public int WebSearchCalls { get; init; }
            public decimal TokenCost { get; init; }
        }

        /// <summary>
        /// Sends one batch to whichever backend the configured model belongs to.
        ///
        /// Data integrity goes down the plain chat path with no tools attached,
        /// which is what keeps it nearly free; the two research checks go to a
        /// Responses endpoint with web search enabled.
        /// </summary>
        private async Task<ModelCallResult> CallModelAsync(
            ContactValidationJob job,
            string prompt,
            CancellationToken cancellationToken)
        {
            var model = job.ModelName ?? AiModelDefaults.ForPurpose(job.CheckType);
            var needsSearch = ValidationCheckTypes.UsesWebSearch(job.CheckType);

            if (LooksLikeDeepSeek(model))
            {
                var request = new EnquiryRequest { Prompt = prompt, ModelName = model };

                // clientId 0: this run already reserved its credits up front, and
                // the pitch service would otherwise deduct one more per batch.
                var result = needsSearch
                    ? await _deepSeekService.GenerateWebSearchAsync(request, 0)
                    : await _deepSeekService.GeneratePitchAsync(request);

                return new ModelCallResult
                {
                    IsSuccess = result.IsSuccess,
                    Content = result.Content ?? "",
                    Error = result.IsSuccess ? null : result.Content,
                    InputTokens = result.PromptTokens,
                    CachedTokens = result.CachedTokens,
                    OutputTokens = result.CompletionTokens,
                    WebSearchCalls = result.WebSearchCalls,
                    TokenCost = result.CurrentCost
                };
            }

            return await CallOpenAiAsync(model, prompt, needsSearch, cancellationToken);
        }

        private async Task<ModelCallResult> CallOpenAiAsync(
            string model,
            string prompt,
            bool needsSearch,
            CancellationToken cancellationToken)
        {
            var rate = await _context.ModelRates.FirstOrDefaultAsync(
                m => m.ModelName == model, cancellationToken);

            var maxTokens = rate?.MaxTokens ?? 8000;

            var body = new Dictionary<string, object>
            {
                ["model"] = model,
                ["input"] = prompt,
                ["max_output_tokens"] = maxTokens
            };

            if (needsSearch)
            {
                body["tools"] = new object[] { new { type = "web_search", external_web_access = true } };
                body["tool_choice"] = "auto";
                body["include"] = new[] { "web_search_call.action.sources" };
            }

            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                "https://api.openai.com/v1/responses");

            request.Headers.Add("Authorization", $"Bearer {_openAiApiKey}");
            request.Headers.Add("Accept", "application/json");
            request.Content = new StringContent(
                JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var json = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return new ModelCallResult
                {
                    IsSuccess = false,
                    Error = $"The model replied {(int)response.StatusCode}: {Truncate(json, 500)}"
                };
            }

            var parsed = JsonConvert.DeserializeObject<JObject>(json)!;

            var content = parsed["output_text"]?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(content))
                content = ExtractResponsesText(parsed);

            var inputTokens = parsed["usage"]?["input_tokens"]?.Value<int>() ?? 0;
            var outputTokens = parsed["usage"]?["output_tokens"]?.Value<int>() ?? 0;
            var cachedTokens =
                parsed["usage"]?["input_tokens_details"]?["cached_tokens"]?.Value<int>() ?? 0;

            var tokenCost =
                (inputTokens * (rate?.InputPrice ?? 0m) / 1_000_000m) +
                (outputTokens * (rate?.OutputPrice ?? 0m) / 1_000_000m);

            // A 200 can still be a truncated generation, and a reasoning model
            // spends the output budget before writing any text. Report that as
            // a failure rather than silently losing a batch of results.
            var incompleteReason = OpenAiResponseGuard.GetIncompleteReason(parsed);

            var searches = CountWebSearchCalls(parsed);

            if (incompleteReason != null || string.IsNullOrWhiteSpace(content))
            {
                return new ModelCallResult
                {
                    IsSuccess = false,
                    Error = OpenAiResponseGuard.DescribeEmptyOutput(incompleteReason, maxTokens),
                    InputTokens = inputTokens,
                    CachedTokens = cachedTokens,
                    OutputTokens = outputTokens,
                    WebSearchCalls = searches,
                    TokenCost = tokenCost
                };
            }

            return new ModelCallResult
            {
                IsSuccess = true,
                Content = content,
                InputTokens = inputTokens,
                CachedTokens = cachedTokens,
                OutputTokens = outputTokens,
                WebSearchCalls = searches,
                TokenCost = tokenCost
            };
        }

        private static int CountWebSearchCalls(JObject parsed)
        {
            if (parsed["output"] is not JArray outputs) return 0;

            return outputs.Count(item =>
                item["type"]?.ToString()?.Contains("web_search", StringComparison.OrdinalIgnoreCase) == true);
        }

        private static string ExtractResponsesText(JObject parsed)
        {
            if (parsed["output"] is not JArray outputs) return "";

            var sb = new StringBuilder();

            foreach (var item in outputs)
            {
                if (item["content"] is not JArray contentArray) continue;

                foreach (var content in contentArray)
                {
                    var text = content["text"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(text))
                        sb.AppendLine(text.Trim());
                }
            }

            return sb.ToString().Trim();
        }

        private static string Truncate(string value, int max) =>
            value.Length <= max ? value : value[..max] + "…";

        // -----------------------------------------------------------------
        // Parsing and persistence
        // -----------------------------------------------------------------

        /// <summary>
        /// Reads the JSON array out of a model reply, tolerating a ```json
        /// fence or a sentence of prose either side of it, and keys the results
        /// by contact ID.
        ///
        /// Field names are read leniently — "Contact Fit confidence",
        /// "contact_fit_confidence" and "ContactFitConfidence" all land on the
        /// same property — because a model that returns the right numbers under
        /// a slightly different spelling has done the work, and throwing that
        /// away would mean paying to run it again.
        /// </summary>
        private static Dictionary<string, ValidationResultItemDto> ParseResults(string content)
        {
            var results = new Dictionary<string, ValidationResultItemDto>(StringComparer.OrdinalIgnoreCase);

            var array = ExtractJsonArray(content);
            if (array == null) return results;

            foreach (var element in array.OfType<JObject>())
            {
                var id = ReadString(element, "ID", "id", "contact_id", "contactId");
                if (string.IsNullOrWhiteSpace(id)) continue;

                results[id.Trim()] = new ValidationResultItemDto
                {
                    ID = id.Trim(),
                    ContactFitConfidence = ReadInt(element,
                        "Contact Fit confidence", "contact_fit_confidence", "ContactFitConfidence"),
                    ContactFitComments = ReadString(element,
                        "Contact Fit comments", "contact_fit_comments", "ContactFitComments"),
                    DataIntegrityConfidence = ReadInt(element,
                        "Data Integrity confidence", "data_integrity_confidence", "DataIntegrityConfidence"),
                    DataIntegrityComments = ReadString(element,
                        "Data Integrity comments", "data_integrity_comments", "DataIntegrityComments"),
                    LiveContactValidityConfidence = ReadInt(element,
                        "Live Contact Validity confidence", "live_contact_validity_confidence",
                        "LiveContactValidityConfidence", "Live Contact confidence"),
                    LiveContactValidityComments = ReadString(element,
                        "Live Contact Validity comments", "live_contact_validity_comments",
                        "LiveContactValidityComments", "Live Contact comments"),
                    CompanyClassification = ReadString(element,
                        "Company classification", "company_classification", "CompanyClassification"),
                    Sources = ReadSources(element)
                };
            }

            return results;
        }

        private static JArray? ExtractJsonArray(string content)
        {
            if (string.IsNullOrWhiteSpace(content)) return null;

            var text = content.Trim();

            // Strip a leading ```json / ``` fence and its closing fence.
            if (text.StartsWith("```", StringComparison.Ordinal))
            {
                var firstLineBreak = text.IndexOf('\n');
                if (firstLineBreak >= 0) text = text[(firstLineBreak + 1)..];

                var closingFence = text.LastIndexOf("```", StringComparison.Ordinal);
                if (closingFence >= 0) text = text[..closingFence];

                text = text.Trim();
            }

            // Fall back to the outermost [ ... ] when prose surrounds the JSON.
            if (!text.StartsWith("[", StringComparison.Ordinal))
            {
                var start = text.IndexOf('[');
                var end = text.LastIndexOf(']');
                if (start < 0 || end <= start) return null;

                text = text[start..(end + 1)];
            }

            try
            {
                return JsonConvert.DeserializeObject<JArray>(text);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string? ReadString(JObject element, params string[] names)
        {
            foreach (var name in names)
            {
                var token = element.GetValue(name, StringComparison.OrdinalIgnoreCase);
                if (token != null && token.Type != JTokenType.Null)
                    return token.ToString();
            }

            return null;
        }

        private static int? ReadInt(JObject element, params string[] names)
        {
            foreach (var name in names)
            {
                var token = element.GetValue(name, StringComparison.OrdinalIgnoreCase);
                if (token == null || token.Type == JTokenType.Null) continue;

                if (token.Type is JTokenType.Integer or JTokenType.Float)
                    return Clamp(token.Value<int>());

                if (int.TryParse(token.ToString().Trim().TrimEnd('%'), out var parsed))
                    return Clamp(parsed);
            }

            return null;

            // The scale is defined as 0-100; anything outside it is a model
            // slip, and clamping keeps the badge and its colour band sane.
            static int Clamp(int value) => Math.Clamp(value, 0, 100);
        }

        private static List<ValidationSourceDto> ReadSources(JObject element)
        {
            var sources = new List<ValidationSourceDto>();

            if (element.GetValue("Sources", StringComparison.OrdinalIgnoreCase) is not JArray array)
                return sources;

            foreach (var item in array.OfType<JObject>())
            {
                var url = ReadString(item, "url", "URL", "link");
                if (string.IsNullOrWhiteSpace(url)) continue;

                // Anything that is not a real link is a hallucinated citation;
                // showing it as clickable evidence would be worse than showing
                // nothing.
                if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed) ||
                    (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
                {
                    continue;
                }

                sources.Add(new ValidationSourceDto
                {
                    Label = ReadString(item, "label", "title", "name")?.Trim() ?? parsed.Host,
                    Url = parsed.ToString()
                });
            }

            return sources;
        }

        private async Task ApplyResultsAsync(
            ContactValidationJob job,
            IEnumerable<Contact> batch,
            IReadOnlyDictionary<int, ContactValidationJobItem> itemsByContact,
            Dictionary<string, ValidationResultItemDto> parsed,
            Dictionary<string, CompanyIntelligence> intelligence,
            CancellationToken cancellationToken)
        {
            var batchList = batch.ToList();
            var contactIds = batchList.Select(c => c.id).ToList();

            var existing = await _context.contact_validations
                .Where(v => v.ClientId == job.ClientId && contactIds.Contains(v.ContactId))
                .ToListAsync(cancellationToken);

            var byContact = existing.ToDictionary(v => v.ContactId);
            var now = DateTime.UtcNow;

            foreach (var contact in batchList)
            {
                if (!itemsByContact.TryGetValue(contact.id, out var item)) continue;

                if (!parsed.TryGetValue(contact.id.ToString(), out var result))
                {
                    // The spec forbids omitting contacts, so a missing one is a
                    // real failure, not a pass. It is recorded and refunded
                    // rather than left looking like an unrun check.
                    item.Status = ValidationItemStatuses.Failed;
                    item.Error = "The model returned no result for this contact.";
                    continue;
                }

                if (!byContact.TryGetValue(contact.id, out var row))
                {
                    row = new ContactValidation
                    {
                        ClientId = job.ClientId,
                        ContactId = contact.id,
                        CreatedAt = now
                    };

                    _context.contact_validations.Add(row);
                    byContact[contact.id] = row;
                }

                // Only the columns belonging to the check that ran are touched.
                switch (job.CheckType)
                {
                    case ValidationCheckTypes.ContactFit:
                        row.ContactFitConfidence = result.ContactFitConfidence;
                        row.ContactFitComments = result.ContactFitComments;
                        row.ContactFitBriefId = job.BriefId;
                        row.ContactFitCheckedAt = now;
                        break;

                    case ValidationCheckTypes.DataIntegrity:
                        row.DataIntegrityConfidence = result.DataIntegrityConfidence;
                        // The empty string is meaningful here: it is how a clean
                        // record is reported, and it must not become null.
                        row.DataIntegrityComments = result.DataIntegrityComments ?? "";
                        row.DataIntegrityCheckedAt = now;
                        break;

                    case ValidationCheckTypes.LiveContact:
                        row.LiveContactConfidence = result.LiveContactValidityConfidence;
                        row.LiveContactComments = result.LiveContactValidityComments;
                        row.LiveContactCheckedAt = now;
                        break;
                }

                row.SourcesJson = MergeSources(row.SourcesJson, result.Sources);
                row.UpdatedAt = now;

                item.Status = ValidationItemStatuses.Completed;
                item.Error = null;

                // Remember what was learned about the employer so the next run
                // does not pay to research it again.
                if (job.CheckType == ValidationCheckTypes.ContactFit &&
                    !string.IsNullOrWhiteSpace(result.CompanyClassification))
                {
                    UpsertCompanyIntelligence(job.ClientId, contact, result, intelligence, now);
                }
            }
        }

        private void UpsertCompanyIntelligence(
            int clientId,
            Contact contact,
            ValidationResultItemDto result,
            Dictionary<string, CompanyIntelligence> intelligence,
            DateTime now)
        {
            var key = CompanyKeyFor(contact);
            if (key == null) return;

            var sourcesJson = JsonConvert.SerializeObject(
                result.Sources ?? new List<ValidationSourceDto>());

            // The dictionary holds every row for these companies, fresh or
            // stale, tracked by the context — so refreshing one is an update in
            // place rather than a second row colliding on the unique index.
            if (intelligence.TryGetValue(key, out var existing))
            {
                if (IsFresh(existing) && !string.IsNullOrWhiteSpace(existing.Classification))
                    return;

                existing.Classification = result.CompanyClassification;
                existing.SourcesJson = sourcesJson;
                existing.ResearchedAt = now;
                return;
            }

            var row = new CompanyIntelligence
            {
                ClientId = clientId,
                Domain = ExtractDomain(contact.website) ?? ExtractDomain(contact.email),
                CompanyNameNormalised = NormaliseText(contact.company_name ?? ""),
                Classification = result.CompanyClassification,
                SourcesJson = sourcesJson,
                ResearchedAt = now
            };

            _context.company_intelligence.Add(row);

            // Added to the dictionary as well, so a second contact at the same
            // company later in this batch updates this row instead of adding
            // another one beside it.
            intelligence[key] = row;
        }

        /// <summary>
        /// Adds new evidence to what a contact already has, keyed on URL so
        /// re-running a check does not stack the same citation up again.
        /// </summary>
        private static string? MergeSources(string? existingJson, List<ValidationSourceDto>? incoming)
        {
            if (incoming == null || incoming.Count == 0)
                return existingJson;

            var merged = new List<ValidationSourceDto>();

            if (!string.IsNullOrWhiteSpace(existingJson))
            {
                try
                {
                    merged = JsonConvert.DeserializeObject<List<ValidationSourceDto>>(existingJson)
                             ?? new List<ValidationSourceDto>();
                }
                catch (JsonException)
                {
                    merged = new List<ValidationSourceDto>();
                }
            }

            foreach (var source in incoming)
            {
                if (merged.Any(m => string.Equals(m.Url, source.Url, StringComparison.OrdinalIgnoreCase)))
                    continue;

                merged.Add(source);
            }

            return JsonConvert.SerializeObject(merged);
        }

        // -----------------------------------------------------------------
        // Email discovery and verification
        // -----------------------------------------------------------------

        /// <summary>
        /// Confirms each address through Prospeo, falling back to Hunter.
        ///
        /// Per contact rather than per batch, because both providers answer
        /// about one person at a time. No model is involved, so this check has
        /// no token cost at all — its cost is the providers' own per-lookup
        /// charge, which they bill directly.
        /// </summary>
        private async Task RunEmailVerificationAsync(
            ContactValidationJob job,
            List<ContactValidationJobItem> items,
            List<Contact> contacts,
            CancellationToken cancellationToken)
        {
            var itemsByContact = items.ToDictionary(i => i.ContactId);
            var contactIds = contacts.Select(c => c.id).ToList();

            var existing = await _context.contact_validations
                .Where(v => v.ClientId == job.ClientId && contactIds.Contains(v.ContactId))
                .ToListAsync(cancellationToken);

            var byContact = existing.ToDictionary(v => v.ContactId);

            foreach (var contact in contacts)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!itemsByContact.TryGetValue(contact.id, out var item)) continue;

                var now = DateTime.UtcNow;

                if (!byContact.TryGetValue(contact.id, out var row))
                {
                    row = new ContactValidation
                    {
                        ClientId = job.ClientId,
                        ContactId = contact.id,
                        CreatedAt = now
                    };

                    _context.contact_validations.Add(row);
                    byContact[contact.id] = row;
                }

                var (confidence, status, source, comments) =
                    await VerifyOneAddressAsync(contact, cancellationToken);

                row.EmailValidityConfidence = confidence;
                row.EmailValidityStatus = status;
                row.EmailValiditySource = source;
                row.EmailValidityComments = comments;
                row.EmailCheckedAt = now;
                row.UpdatedAt = now;

                item.Status = ValidationItemStatuses.Completed;
                item.Error = null;

                job.ProcessedCount = items.Count(i => i.Status == ValidationItemStatuses.Completed);
                await _context.SaveChangesAsync(cancellationToken);
            }
        }

        private async Task<(int Confidence, string? Status, string Source, string Comments)>
            VerifyOneAddressAsync(Contact contact, CancellationToken cancellationToken)
        {
            // Prospeo matches on a LinkedIn profile, so a contact without one
            // goes straight to Hunter rather than spending a lookup that cannot
            // succeed.
            if (!string.IsNullOrWhiteSpace(contact.linkedin_url) && _prospeoService.IsConfigured)
            {
                var prospeo = await _prospeoService.FindEmailAsync(contact.linkedin_url!, cancellationToken);

                if (prospeo.Found)
                {
                    var matchesStored = string.Equals(
                        prospeo.Email, contact.email?.Trim(), StringComparison.OrdinalIgnoreCase);

                    // A verified address that differs from the stored one is not
                    // a pass: the record on file is still the wrong address, and
                    // saying so is the whole value of the check.
                    return matchesStored
                        ? (98, prospeo.EmailStatus, "prospeo",
                           "Prospeo verified the address on file.")
                        : (60, prospeo.EmailStatus, "prospeo",
                           $"Prospeo verified a different address for this person: {prospeo.Email}. The address on file may be out of date.");
                }
            }

            if (_hunterService.IsConfigured)
            {
                var hunter = await _hunterService.FindEmailAsync(
                    new HunterLookupRequest
                    {
                        FullName = contact.full_name ?? $"{contact.first_name} {contact.last_name}".Trim(),
                        CompanyUrl = contact.website,
                        Company = contact.company_name,
                        EmailHint = contact.email
                    },
                    cancellationToken);

                if (hunter.Found)
                {
                    var matchesStored = string.Equals(
                        hunter.Email, contact.email?.Trim(), StringComparison.OrdinalIgnoreCase);

                    return matchesStored
                        ? (hunter.Score, hunter.VerificationStatus, "hunter",
                           $"Hunter confirmed the address on file with a confidence of {hunter.Score}.")
                        : (Math.Min(hunter.Score, 60), hunter.VerificationStatus, "hunter",
                           $"Hunter found a different address for this person: {hunter.Email}. The address on file may be out of date.");
                }

                return (10, null, "hunter",
                    hunter.RejectedBecause ?? "Neither provider could confirm an address for this contact.");
            }

            return (0, null, "none",
                "No email verification provider is configured. An admin needs to add a Prospeo or Hunter API key.");
        }

        // =================================================================
        // Mapping
        // =================================================================

        public static ValidationJobDto ToDto(ContactValidationJob job) => new()
        {
            Id = job.Id,
            CheckType = job.CheckType,
            Status = job.Status,
            BriefId = job.BriefId,
            ModelName = job.ModelName,
            Provider = job.Provider,
            ContactCount = job.ContactCount,
            ProcessedCount = job.ProcessedCount,
            FailedCount = job.FailedCount,
            InputTokens = job.InputTokens,
            CachedTokens = job.CachedTokens,
            OutputTokens = job.OutputTokens,
            TotalTokens = job.TotalTokens,
            WebSearchCalls = job.WebSearchCalls,
            CalculatedCost = job.CalculatedCost,
            CreditsCharged = job.CreditsCharged,
            ElapsedMs = job.ElapsedMs,
            ErrorMessage = job.ErrorMessage,
            CreatedAt = job.CreatedAt,
            StartedAt = job.StartedAt,
            CompletedAt = job.CompletedAt
        };
    }
}
