namespace PitchGenApi.Services
{
    using System.Diagnostics;
    using System.Text;
    using System.Threading.Channels;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.DependencyInjection;
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
        /// <summary>Contacts covered by one credit.</summary>
        private const int ContactsPerCredit = 10;

        /// <summary>
        /// How long one batch may wait on a provider before it is abandoned.
        ///
        /// The HttpClient timeout is ten minutes, which is a ceiling for a
        /// request, not a budget for a queue: a single wedged call at that
        /// length holds a runner slot long enough for every other client to
        /// notice. Three minutes is generous for fifty contacts even with web
        /// search, and it stays well inside <see cref="DefaultStaleAfter"/> so
        /// a slow batch is never mistaken for a dead one.
        /// </summary>
        /// <summary>
        /// Appended to a research prompt on the retry after a batch came back
        /// with no searches. Deliberately blunt, and deliberately says what to
        /// do when nothing is found: the failure mode it answers is a model
        /// that decided the contacts were not worth looking up, and telling it
        /// to score low without evidence is what stops it skipping the search
        /// to "help".
        /// </summary>
        private const string SearchRequiredReminder =
            "\n\nIMPORTANT: You did not search the web on the previous attempt. " +
            "You MUST use the web_search tool for every contact above before you " +
            "answer, even when you believe you already know the answer, and even " +
            "when the company or title looks unfamiliar or the record looks " +
            "incomplete. If a search returns nothing useful for a contact, say so " +
            "in that contact's comment and score it low — do not skip the search.";

        private static readonly TimeSpan DefaultModelCallTimeout = TimeSpan.FromMinutes(3);

        /// <summary>
        /// How quiet a running job must go before it is treated as abandoned.
        /// Must stay comfortably above <see cref="DefaultModelCallTimeout"/>,
        /// or a legitimately slow batch would be requeued underneath itself and
        /// run twice.
        /// </summary>
        private static readonly TimeSpan DefaultStaleAfter = TimeSpan.FromMinutes(10);

        /// <summary>
        /// Claims allowed before a job is failed for good. A run that reliably
        /// kills its process would otherwise be requeued forever, taking a
        /// runner slot with it every time.
        /// </summary>
        private const int DefaultMaxAttempts = 3;

        /// <summary>
        /// Provider calls allowed in flight at once, across every run in this
        /// process — not per job. Process-wide on purpose: the job runner no
        /// longer caps how many runs execute together, so a per-job limit would
        /// multiply by however many clients happened to press the button at the
        /// same moment and put the providers into rate limiting, which costs
        /// tokens without producing results.
        /// </summary>
        private const int DefaultMaxParallelBatches = 10;

        /// <summary>Hard ceiling on the configured value; see the note above.</summary>
        private const int MaxParallelBatchesCeiling = 64;

        /// <summary>
        /// How often a run touches its heartbeat while nothing has come back
        /// yet. A batch can now take its own timeout plus a retry before it
        /// reports, which is long enough for the reaper to decide the run was
        /// abandoned and requeue it underneath itself.
        /// </summary>
        private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

        /// <summary>Pause before the single retry, and the longer one used when the provider said it was being throttled.</summary>
        private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan ThrottledRetryDelay = TimeSpan.FromSeconds(10);

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
        private readonly IValidationSettingsService _validationSettings;
        private readonly IProspeoEmailService _prospeoService;
        private readonly IHunterEmailService _hunterService;

        /// <summary>
        /// Batches run their provider calls off this thread, and both pitch
        /// services hold the same scoped <see cref="AppDbContext"/> as this one
        /// — they read ModelRates on every call — so they cannot be shared
        /// across them. Each batch takes a scope of its own instead.
        /// </summary>
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly HttpClient _httpClient;
        private readonly IConfiguration _configuration;
        private readonly ILogger<ContactValidationService> _logger;
        private readonly string _openAiApiKey;

        public ContactValidationService(
            AppDbContext context,
            ContactRepository contactRepository,
            IAiModelSettingsService aiModelSettings,
            IPromptSettingsService promptSettings,
            IValidationSettingsService validationSettings,
            IProspeoEmailService prospeoService,
            IHunterEmailService hunterService,
            IServiceScopeFactory scopeFactory,
            HttpClient httpClient,
            IConfiguration configuration,
            IOptions<OpenAISettings> openAiOptions,
            ILogger<ContactValidationService> logger)
        {
            _context = context;
            _contactRepository = contactRepository;
            _aiModelSettings = aiModelSettings;
            _promptSettings = promptSettings;
            _validationSettings = validationSettings;
            _prospeoService = prospeoService;
            _hunterService = hunterService;
            _scopeFactory = scopeFactory;
            _httpClient = httpClient;
            _configuration = configuration;
            _logger = logger;
            _openAiApiKey = openAiOptions.Value.ApiKey;
        }

        /// <summary>
        /// Contacts per model request, as the admin page has it — read once
        /// per run rather than held, so moving the number takes effect on the
        /// next run without a redeploy or a restart.
        /// </summary>
        private Task<int> GetBatchSizeAsync(CancellationToken cancellationToken) =>
            _validationSettings.GetBatchSizeAsync(cancellationToken);

        /// <summary>
        /// What one server-side web search costs, in dollars. Not returned by
        /// the providers, so it has to be configured; without it a run's
        /// reported cost would only ever show the near-zero token half.
        /// </summary>

        private TimeSpan ModelCallTimeout
        {
            get
            {
                var seconds = _configuration.GetValue<int?>("Validation:ModelCallTimeoutSeconds");
                return seconds is > 0 ? TimeSpan.FromSeconds(seconds.Value) : DefaultModelCallTimeout;
            }
        }

        private TimeSpan StaleAfter
        {
            get
            {
                var minutes = _configuration.GetValue<int?>("Validation:StaleJobMinutes");
                return minutes is > 0 ? TimeSpan.FromMinutes(minutes.Value) : DefaultStaleAfter;
            }
        }

        private int MaxAttempts
        {
            get
            {
                var configured = _configuration.GetValue<int?>("Validation:MaxAttempts");
                return configured is > 0 ? configured.Value : DefaultMaxAttempts;
            }
        }

        private int MaxParallelBatches
        {
            get
            {
                var configured = _configuration.GetValue<int?>("Validation:MaxParallelBatches");

                return configured is > 0
                    ? Math.Min(configured.Value, MaxParallelBatchesCeiling)
                    : DefaultMaxParallelBatches;
            }
        }

        /// <summary>
        /// The process-wide limit on provider calls in flight.
        ///
        /// Created once and never resized. Handing out a second semaphore while
        /// the first still has permits out would let both run at full width at
        /// the same time, which is the one failure this is here to prevent — so
        /// a change to the setting takes effect on the next restart, exactly as
        /// the hard-coded limit it replaces did.
        /// </summary>
        private SemaphoreSlim ProviderGate
        {
            get
            {
                if (_providerGate != null)
                    return _providerGate;

                lock (ProviderGateLock)
                {
                    var width = MaxParallelBatches;
                    return _providerGate ??= new SemaphoreSlim(width, width);
                }
            }
        }

        private static readonly object ProviderGateLock = new();
        private static SemaphoreSlim? _providerGate;

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

            if (LooksLikeDeepSeek(modelName)) return "deepseek";
            if (LooksLikeQwen(modelName)) return "qwen";

            return "openai";
        }

        private static bool LooksLikeDeepSeek(string? modelName) =>
            modelName?.StartsWith("deepseek", StringComparison.OrdinalIgnoreCase) == true;

        private static bool LooksLikeQwen(string? modelName) =>
            modelName?.StartsWith("qwen", StringComparison.OrdinalIgnoreCase) == true;

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

        /// <summary>
        /// The claim itself: one UPDATE, guarded by ROWLOCK/UPDLOCK so two
        /// runner instances racing this at once cannot both grab the same row,
        /// and READPAST so one racing the other simply skips what it can't
        /// lock instead of blocking on it. This is the statement that makes
        /// the job's status trustworthy — before this, "queued" in the
        /// database and "already being worked" in memory could both be true
        /// at once, which is what job 66 showed on the API response.
        /// </summary>
        public async Task<List<int>> ClaimQueuedJobsAsync(int maxJobs, string owner, CancellationToken cancellationToken = default)
        {
            if (maxJobs <= 0)
                return new List<int>();

            var now = DateTime.UtcNow;

            // UPDATE TOP (n) alone has no ORDER BY, so on its own it would
            // claim an arbitrary set rather than the oldest. The ordering and
            // the row locks both live in the inner subquery — READPAST there
            // means one runner instance racing another simply skips whatever
            // it cannot lock rather than blocking on it — and the outer UPDATE
            // joins onto exactly those ids to set the real columns.
            var claimed = await _context.Database.SqlQuery<int>($@"
                UPDATE t
                SET status = {ValidationJobStatuses.Running},
                    started_at = {now},
                    heartbeat_at = {now},
                    owner = {owner},
                    attempts = t.attempts + 1
                OUTPUT inserted.id
                FROM contact_validation_jobs AS t
                INNER JOIN (
                    SELECT TOP ({maxJobs}) id
                    FROM contact_validation_jobs WITH (READPAST, UPDLOCK, ROWLOCK)
                    WHERE status = {ValidationJobStatuses.Queued}
                    ORDER BY created_at
                ) AS next_jobs ON next_jobs.id = t.id
            ").ToListAsync(cancellationToken);

            return claimed;
        }

        /// <summary>
        /// Recovers runs whose process died without saying so. A job stays
        /// "running" but its heartbeat stops the moment the worker that owned
        /// it goes away — a deploy, an app-pool recycle, an unhandled crash —
        /// and nothing else ever notices, so the job sits there indefinitely,
        /// its reserved credits never refunded and its slot never freed.
        ///
        /// A job under <see cref="MaxAttempts"/> goes back to "queued" for
        /// another try. One at the limit is failed outright and refunded,
        /// rather than requeued forever.
        /// </summary>
        public async Task<int> ReapStaleJobsAsync(CancellationToken cancellationToken = default)
        {
            var cutoff = DateTime.UtcNow - StaleAfter;

            var stale = await _context.contact_validation_jobs
                .Where(j => j.Status == ValidationJobStatuses.Running &&
                            (j.HeartbeatAt ?? j.StartedAt ?? j.CreatedAt) < cutoff)
                .ToListAsync(cancellationToken);

            if (stale.Count == 0)
                return 0;

            foreach (var job in stale)
            {
                if (job.Attempts >= MaxAttempts)
                {
                    job.Status = ValidationJobStatuses.Failed;
                    job.ErrorMessage =
                        $"Abandoned by the worker that was running it, and not recovered after {job.Attempts} attempt(s).";
                    job.CompletedAt = DateTime.UtcNow;

                    await RefundUnearnedCreditsAsync(job, cancellationToken);

                    _logger.LogWarning(
                        "Validation job {JobId} failed after {Attempts} stale attempts.",
                        job.Id, job.Attempts);
                }
                else
                {
                    // Back to the queue rather than resumed in place: the batch
                    // in flight when the process died is of unknown state, and
                    // ProcessJobAsync re-reads every item fresh on its next run
                    // rather than trusting what a dead worker left behind.
                    job.Status = ValidationJobStatuses.Queued;
                    job.StartedAt = null;
                    job.HeartbeatAt = null;
                    job.Owner = null;

                    _logger.LogWarning(
                        "Validation job {JobId} requeued after going stale (attempt {Attempts}).",
                        job.Id, job.Attempts);
                }
            }

            await _context.SaveChangesAsync(cancellationToken);
            return stale.Count;
        }

        public async Task ProcessJobAsync(int jobId, CancellationToken cancellationToken = default)
        {
            var job = await _context.contact_validation_jobs
                .FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken);

            if (job == null)
                return;

            var timer = Stopwatch.StartNew();

            try
            {
                // Normally the runner has already flipped this to running as
                // part of claiming it; this only covers a direct call. Either
                // way the heartbeat starts here, and it is inside the try so a
                // failure to write it cannot strand the job the way it used to
                // — the reaper will find it and put it back.
                if (job.Status != ValidationJobStatuses.Running)
                {
                    job.Status = ValidationJobStatuses.Running;
                    job.StartedAt = DateTime.UtcNow;
                }

                job.HeartbeatAt = DateTime.UtcNow;
                await _context.SaveChangesAsync(cancellationToken);

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

                // A run whose batches all failed threw nothing, so without this
                // the job carries no reason at all and the log can only say
                // "failed". The per-item errors hold the answer; lift the most
                // common one onto the job.
                if (job.FailedCount > 0 && string.IsNullOrWhiteSpace(job.ErrorMessage))
                {
                    job.ErrorMessage = MostCommonItemError(items);
                }
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
                job.HeartbeatAt = DateTime.UtcNow;

                // In the finally so a crashed run refunds too. Charging for
                // fifty contacts after processing ten would be the worst
                // possible failure mode of a paid feature.
                await RefundUnearnedCreditsAsync(job, CancellationToken.None);

                try
                {
                    // Deliberately not the job's token. On shutdown that token
                    // is already cancelled, and passing it here is what left
                    // job 2 stuck at "running" for two days: the work had
                    // stopped but the row never said so.
                    await _context.SaveChangesAsync(CancellationToken.None);
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

            var batchSize = await GetBatchSizeAsync(cancellationToken);

            // Whether this check is only meaningful with live evidence. Used
            // below to reject an answer that came back without having searched.
            var needsSearch = ValidationCheckTypes.UsesWebSearch(job.CheckType);

            // Loaded once for the whole run rather than once per batch. It has
            // to be: it is an EF read returning tracked entities, and the
            // batches no longer run on this thread. Loading it here is also
            // fewer queries and one shared row per employer, so two batches at
            // the same company can no longer each add one.
            var intelligence = job.CheckType == ValidationCheckTypes.ContactFit
                ? await LoadCompanyIntelligenceAsync(job.ClientId, contacts, cancellationToken)
                : new Dictionary<string, CompanyIntelligence>();

            // Every prompt is built here, before anything is dispatched, so the
            // parallel region below touches no tracked entity at all - only the
            // strings it is handed. The company notes are still narrowed to each
            // batch's own employers; describing all of them in every prompt
            // would grow the input with the size of the run.
            var work = contacts
                .Chunk(batchSize)
                .Select(batch => (
                    Batch: batch,
                    Prompt: BuildPrompt(
                        promptTemplate,
                        briefText,
                        duplicateFlags,
                        DescribeCompanyIntelligence(NarrowToBatch(intelligence, batch)),
                        BuildContactsJson(batch))))
                .ToList();

            if (work.Count == 0)
                return;

            // Producer/consumer rather than a plain parallel loop over the
            // batches. Every database write in a run has to stay on this
            // thread: the job's DbContext is not thread-safe, and neither are
            // the job row and the item rows it is tracking. So the batches do
            // the provider work in parallel and hand back plain objects, and
            // the loop below is the only thing in the run that touches EF.
            var channel = Channel.CreateUnbounded<BatchOutcome>(
                new UnboundedChannelOptions { SingleReader = true });

            var producer = Task.Run(async () =>
            {
                try
                {
                    await Parallel.ForEachAsync(
                        work,
                        new ParallelOptions
                        {
                            // Bounded here as well as by the process-wide gate:
                            // without it a ten-thousand-contact run would queue
                            // a task per batch up front, all of them waiting on
                            // the same gate.
                            MaxDegreeOfParallelism = MaxParallelBatches,
                            CancellationToken = cancellationToken
                        },
                        async (unit, token) =>
                        {
                            await ProviderGate.WaitAsync(token);

                            try
                            {
                                var outcome = await RunOneBatchAsync(
                                    job, unit.Batch, unit.Prompt, needsSearch, token);

                                // Never the batch's own token: a result that has
                                // already been paid for must reach the consumer
                                // even as the run is being cancelled.
                                await channel.Writer.WriteAsync(outcome, CancellationToken.None);
                            }
                            finally
                            {
                                ProviderGate.Release();
                            }
                        });
                }
                finally
                {
                    channel.Writer.Complete();
                }
            }, cancellationToken);

            var reader = channel.Reader;

            try
            {
                while (true)
                {
                    bool hasMore;

                    // The wait is bounded so a run whose batches are all still
                    // in flight keeps its heartbeat current. A batch can now
                    // take its own timeout and then a retry before it reports
                    // anything, and without this tick the reaper would call the
                    // run abandoned and requeue it underneath itself.
                    using (var idle = new CancellationTokenSource(HeartbeatInterval))
                    using (var linked = CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken, idle.Token))
                    {
                        try
                        {
                            hasMore = await reader.WaitToReadAsync(linked.Token);
                        }
                        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                        {
                            job.HeartbeatAt = DateTime.UtcNow;
                            await _context.SaveChangesAsync(cancellationToken);
                            continue;
                        }
                    }

                    if (!hasMore)
                        break;

                    // Drained rather than taken one at a time: several batches
                    // finishing together become one save instead of several.
                    while (reader.TryRead(out var outcome))
                    {
                        await ApplyBatchOutcomeAsync(
                            job, outcome, itemsByContact, intelligence, cancellationToken);
                    }

                    // Saved as results arrive, so a long run shows progress as
                    // it goes and a crash costs only what was still in flight.
                    job.ProcessedCount = items.Count(i => i.Status == ValidationItemStatuses.Completed);
                    job.HeartbeatAt = DateTime.UtcNow;
                    await _context.SaveChangesAsync(cancellationToken);
                }
            }
            catch
            {
                // The consumer left early, so the batches still in flight have
                // nobody reading for them. They are waited on before the
                // failure travels any further: ProcessJobAsync is about to
                // rewrite the job row those batches are still reading, and a
                // dropped producer task would take its own exception with it.
                await DrainAsync(producer);
                throw;
            }

            // Surfaces a cancellation, and anything RunOneBatchAsync could not
            // turn into an outcome, instead of leaving it on a dropped task.
            await producer;
        }

        /// <summary>
        /// Waits for a producer whose consumer has already failed. Its
        /// exception is deliberately dropped: the consumer's is the one that
        /// describes what went wrong, and this is only here so nothing is still
        /// running when the run is finalised.
        /// </summary>
        private static async Task DrainAsync(Task producer)
        {
            try
            {
                await producer;
            }
            catch
            {
                // Nothing to add; the caller is already rethrowing.
            }
        }

        /// <summary>
        /// The company notes belonging to one batch's employers.
        ///
        /// The cache is loaded for the whole run, but a prompt may only carry
        /// the companies its own contacts work for - otherwise every batch
        /// would restate every employer in the run and the input would grow
        /// with the square of the selection.
        /// </summary>
        private static Dictionary<string, CompanyIntelligence> NarrowToBatch(
            Dictionary<string, CompanyIntelligence> intelligence,
            IEnumerable<Contact> batch)
        {
            if (intelligence.Count == 0)
                return intelligence;

            var narrowed = new Dictionary<string, CompanyIntelligence>();

            foreach (var contact in batch)
            {
                var key = CompanyKeyFor(contact);

                if (key != null && intelligence.TryGetValue(key, out var row))
                    narrowed[key] = row;
            }

            return narrowed;
        }

        /// <summary>
        /// Folds one finished batch into the run. Called only from the run's
        /// own thread - everything it touches is tracked by the job's context.
        /// </summary>
        private async Task ApplyBatchOutcomeAsync(
            ContactValidationJob job,
            BatchOutcome outcome,
            IReadOnlyDictionary<int, ContactValidationJobItem> itemsByContact,
            Dictionary<string, CompanyIntelligence> intelligence,
            CancellationToken cancellationToken)
        {
            // Counted before the branch: a batch that failed still spent what
            // it spent, and a retry is billed whether or not it helped.
            job.InputTokens += outcome.InputTokens;
            job.CachedTokens += outcome.CachedTokens;
            job.OutputTokens += outcome.OutputTokens;
            job.TotalTokens += outcome.InputTokens + outcome.OutputTokens;
            job.WebSearchCalls += outcome.WebSearchCalls;

            // Tokens only. Both providers bill server-side search as the extra
            // model tokens it consumes, and those tokens are already in the
            // usage figures above, so a per-call fee on top invents cost nobody
            // charged - a 53-search run reported $0.53 of fee against roughly
            // $0.34 of actual tokens, more than doubling the number every
            // pricing decision was being read from.
            job.CalculatedCost += outcome.TokenCost;

            if (outcome.SearchEvidence.Count > 0)
            {
                _logger.LogInformation(
                    "Validation job {JobId} batch searched: {Evidence}",
                    job.Id,
                    string.Join(" | ", outcome.SearchEvidence));
            }

            if (outcome.Error != null || outcome.Parsed == null)
            {
                MarkBatchFailed(
                    outcome.Contacts,
                    itemsByContact,
                    outcome.Error ?? "The model returned nothing.");

                return;
            }

            await ApplyResultsAsync(
                job, outcome.Contacts, itemsByContact, outcome.Parsed, intelligence, cancellationToken);
        }

        /// <summary>
        /// One batch, end to end, with a single retry - and no access to the
        /// job's database context, because this does not run on the job's
        /// thread.
        ///
        /// Two provider calls is the ceiling, whatever went wrong. The old code
        /// could reach three on a search check by stacking its search retry on
        /// top of the call itself, and a run that keeps paying for the same
        /// unusable answer is worse than one that reports the failure. What is
        /// new is that an unreadable reply is retried at all: it used to kill
        /// its batch outright on the first attempt, which is how a run of
        /// thirty came back having done twenty-five.
        ///
        /// Nothing here throws except cancellation. A batch that cannot be
        /// rescued comes back as a failed outcome and the rest of the run
        /// carries on without it.
        /// </summary>
        private async Task<BatchOutcome> RunOneBatchAsync(
            ContactValidationJob job,
            Contact[] batch,
            string prompt,
            bool needsSearch,
            CancellationToken cancellationToken)
        {
            // Its own scope, and so its own DbContext: both pitch services read
            // ModelRates through the context they were constructed with, and
            // that is the run's own context for every batch unless this is here.
            using var scope = new ProviderScope(_scopeFactory);

            var usage = new UsageTotals();
            var attemptPrompt = prompt;
            AttemptResult result;

            for (var attempt = 1; ; attempt++)
            {
                result = await AttemptBatchAsync(
                    job, scope, batch, attemptPrompt, needsSearch, usage, cancellationToken);

                if (result.Failure == null || attempt == 2)
                    break;

                _logger.LogWarning(
                    "Validation job {JobId}: a batch of {Count} contact(s) failed ({Reason}). Retrying once.",
                    job.Id, batch.Length, Truncate(result.Failure, 200));

                // A model that simply decided it already knew gets the
                // requirement spelled out on the way back in. Measured on
                // deepseek-v4-flash, an identical request searches on most runs
                // and occasionally does not, and a batch should not be lost to
                // that coin flip.
                attemptPrompt = result.RetryWithSearchReminder
                    ? prompt + SearchRequiredReminder
                    : prompt;

                await Task.Delay(result.RetryDelay, cancellationToken);
            }

            return new BatchOutcome
            {
                Contacts = batch,
                Parsed = result.Parsed,
                Error = result.Failure,
                SearchEvidence = result.Evidence,
                InputTokens = usage.InputTokens,
                CachedTokens = usage.CachedTokens,
                OutputTokens = usage.OutputTokens,
                WebSearchCalls = usage.WebSearchCalls,
                TokenCost = usage.TokenCost
            };
        }

        /// <summary>
        /// One provider call and everything that decides whether its answer is
        /// usable. Usage is added to <paramref name="usage"/> whether or not it
        /// was, because it was billed either way.
        /// </summary>
        private async Task<AttemptResult> AttemptBatchAsync(
            ContactValidationJob job,
            ProviderScope scope,
            Contact[] batch,
            string prompt,
            bool needsSearch,
            UsageTotals usage,
            CancellationToken cancellationToken)
        {
            ModelCallResult call;

            // Bounded independently of HttpClient's own (much longer) timeout: a
            // batch that hangs this long would otherwise hold a permit on the
            // provider gate and keep every other run's batches waiting behind it.
            using var batchTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            batchTimeout.CancelAfter(ModelCallTimeout);

            try
            {
                call = await CallModelAsync(job, scope, prompt, batch.Length, batchTimeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return AttemptResult.Failed(
                    $"The model call timed out after {ModelCallTimeout.TotalSeconds:0}s.");
            }
            catch (Exception ex)
            {
                return AttemptResult.Failed("The model call failed: " + ex.Message);
            }

            usage.Add(call);

            if (!call.IsSuccess)
                return AttemptResult.Failed(call.Error ?? "The model returned nothing.");

            // A research check that did not search answered from training data,
            // and it does so over HTTP 200 with well-formed JSON - there is
            // nothing further downstream that can tell the difference. Fail it
            // here instead of writing a verdict that looks identical to a
            // verified one.
            //
            // This is the guard that matters when a model alias changes under
            // us: DeepSeek's compatibility table lists web_search among built-in
            // tools it ignores, and deepseek-v4-pro is routed to V4.1-Flash
            // after 2026-09-14. Whichever way that lands, it surfaces here as a
            // failed batch rather than as silently invented verification.
            if (needsSearch && call.WebSearchCalls == 0)
            {
                var returned = call.OutputItemTypes.Count > 0
                    ? string.Join(", ", call.OutputItemTypes)
                    : "nothing";

                // "asked to use it", not "with tool_choice forcing it": that was
                // only ever true of the OpenAI path. Qwen accepts tool_choice
                // and ignores it for built-in tools, so on that provider the old
                // wording sent the reader looking for a broken parameter when
                // the model had simply declined.
                return AttemptResult.Failed(
                    $"The model answered without searching the web, so the result is not " +
                    $"verified evidence. Model '{job.ModelName}' was sent the web_search tool " +
                    $"and asked to use it, and returned no search items. " +
                    $"The response contained: {returned}.",
                    retryWithSearchReminder: true);
            }

            var parsed = ParseResults(call.Content, job.CheckType);

            if (parsed.Count == 0)
            {
                // Logged with the reply itself: an unreadable answer is the one
                // failure whose cause is never in the message, and a truncated
                // array and a model that answered in prose look identical from
                // the outside.
                _logger.LogWarning(
                    "Validation job {JobId} ({CheckType}): a batch of {Count} contact(s) " +
                    "returned no readable JSON. Reply begins: {Sample}",
                    job.Id, job.CheckType, batch.Length, Truncate(call.Content, 1500));

                return AttemptResult.Failed("The model's reply could not be read as JSON results.");
            }

            // A batch that carries no corrections is indistinguishable
            // downstream from a batch with nothing to correct: both store null.
            // That makes the two failures that matter look identical - a model
            // that never emitted the key, and a parser that rejected every item
            // it did. Logging the reply on that path is what tells them apart,
            // and it costs one line on a batch that had nothing to say anyway.
            if (parsed.Values.All(r => (r.Suggestions?.Count ?? 0) == 0))
            {
                _logger.LogWarning(
                    "Validation job {JobId} ({CheckType}): a batch of {Count} contact(s) " +
                    "returned no usable corrections. Reply begins: {Sample}",
                    job.Id, job.CheckType, batch.Length, Truncate(call.Content, 1500));
            }

            return new AttemptResult { Parsed = parsed, Evidence = call.SearchEvidence };
        }

        /// <summary>
        /// The error that stopped the most contacts, so a job that failed the
        /// same way fifty times reports that reason once.
        /// </summary>
        private static string? MostCommonItemError(IEnumerable<ContactValidationJobItem> items)
        {
            var error = items
                .Where(i => i.Status == ValidationItemStatuses.Failed &&
                            !string.IsNullOrWhiteSpace(i.Error))
                .GroupBy(i => i.Error!)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key)
                .FirstOrDefault();

            return error == null ? null : Truncate(error, 500);
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

        /// <summary>
        /// A scope of its own for one batch's provider calls.
        ///
        /// Both pitch services take the scoped <see cref="AppDbContext"/> and
        /// query ModelRates on every call, and <see cref="CallOpenAiAsync"/>
        /// does the same. Sharing the run's context across batches running at
        /// once is what EF means by "a second operation was started on this
        /// context instance", so each batch resolves its own.
        /// </summary>
        private sealed class ProviderScope : IDisposable
        {
            private readonly IServiceScope _scope;

            public ProviderScope(IServiceScopeFactory factory)
            {
                _scope = factory.CreateScope();

                Context = _scope.ServiceProvider.GetRequiredService<AppDbContext>();
                DeepSeek = _scope.ServiceProvider.GetRequiredService<DeepSeekPitchService>();
                Qwen = _scope.ServiceProvider.GetRequiredService<QwenPitchService>();
            }

            public AppDbContext Context { get; }
            public DeepSeekPitchService DeepSeek { get; }
            public QwenPitchService Qwen { get; }

            public void Dispose() => _scope.Dispose();
        }

        /// <summary>
        /// What one batch spent. Mutable and unsynchronised on purpose: it
        /// belongs to a single batch task and is read only once that task has
        /// finished, so the job's own totals are never touched off-thread.
        /// </summary>
        private sealed class UsageTotals
        {
            public int InputTokens;
            public int CachedTokens;
            public int OutputTokens;
            public int WebSearchCalls;
            public decimal TokenCost;

            public void Add(ModelCallResult call)
            {
                InputTokens += call.InputTokens;
                CachedTokens += call.CachedTokens;
                OutputTokens += call.OutputTokens;
                WebSearchCalls += call.WebSearchCalls;
                TokenCost += call.TokenCost;
            }
        }

        /// <summary>One provider call, judged. <see cref="Failure"/> null means usable.</summary>
        private sealed class AttemptResult
        {
            public string? Failure { get; init; }
            public Dictionary<string, ValidationResultItemDto>? Parsed { get; init; }
            public IReadOnlyList<string> Evidence { get; init; } = Array.Empty<string>();

            /// <summary>Whether the retry should spell out that searching is required.</summary>
            public bool RetryWithSearchReminder { get; init; }

            public TimeSpan RetryDelay { get; init; } = ContactValidationService.RetryDelay;

            public static AttemptResult Failed(string failure, bool retryWithSearchReminder = false) =>
                new()
                {
                    Failure = failure,
                    RetryWithSearchReminder = retryWithSearchReminder,

                    // A provider that said it was throttling gets longer than
                    // one that simply answered badly. Retrying a rate limit two
                    // seconds later just spends the batch's second chance on
                    // the same refusal.
                    RetryDelay = LooksThrottled(failure) ? ThrottledRetryDelay : ContactValidationService.RetryDelay
                };

            private static bool LooksThrottled(string failure) =>
                failure.Contains("429", StringComparison.Ordinal) ||
                failure.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ||
                failure.Contains("too many requests", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// One finished batch on its way back to the run's thread. Everything
        /// here is a plain value or a contact the run loaded read-only, so
        /// nothing tracked by the job's context crosses a thread boundary.
        /// </summary>
        private sealed class BatchOutcome
        {
            public Contact[] Contacts { get; init; } = Array.Empty<Contact>();
            public Dictionary<string, ValidationResultItemDto>? Parsed { get; init; }

            /// <summary>Null when the batch succeeded; otherwise why it failed after its retry.</summary>
            public string? Error { get; init; }

            public IReadOnlyList<string> SearchEvidence { get; init; } = Array.Empty<string>();
            public int InputTokens { get; init; }
            public int CachedTokens { get; init; }
            public int OutputTokens { get; init; }
            public int WebSearchCalls { get; init; }
            public decimal TokenCost { get; init; }
        }

        private sealed class ModelCallResult
        {
            public bool IsSuccess { get; init; }
            public string Content { get; init; } = "";
            public string? Error { get; init; }
            public int InputTokens { get; init; }
            public int CachedTokens { get; init; }
            public int OutputTokens { get; init; }
            public int WebSearchCalls { get; init; }
            public IReadOnlyList<string> SearchEvidence { get; init; } = Array.Empty<string>();
            public IReadOnlyList<string> OutputItemTypes { get; init; } = Array.Empty<string>();
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
            ProviderScope scope,
            string prompt,
            int batchCount,
            CancellationToken cancellationToken)
        {
            var model = job.ModelName ?? AiModelDefaults.ForPurpose(job.CheckType);
            var needsSearch = ValidationCheckTypes.UsesWebSearch(job.CheckType);

            // DeepSeek and Qwen share this branch because they share a service
            // shape — the same two methods returning the same PitchResult — so
            // only the dispatch below differs between them. OpenAI does not:
            // its check is assembled request-by-request in CallOpenAiAsync.
            if (LooksLikeDeepSeek(model) || LooksLikeQwen(model))
            {
                var providerRate = await scope.Context.ModelRates.FirstOrDefaultAsync(
                    m => m.ModelName == model, cancellationToken);

                // Math.Max, not a plain assignment: EnquiryRequest.MaxTokens wins
                // outright inside the pitch service, so assigning the computed
                // budget here would silently discard a larger configured
                // ModelRates.MaxTokens. The OpenAI path below takes the larger of
                // the two; this one has to agree with it.
                var request = new EnquiryRequest
                {
                    Prompt = prompt,
                    ModelName = model,
                    MaxTokens = Math.Max(
                        providerRate?.MaxTokens ?? 0,
                        OutputBudgetFor(batchCount, needsSearch))
                };

                // clientId 0: this run already reserved its credits up front, and
                // the pitch service would otherwise deduct one more per batch.
                var result = LooksLikeQwen(model)
                    ? (needsSearch
                        ? await scope.Qwen.GenerateWebSearchAsync(request, 0)
                        : await scope.Qwen.GeneratePitchAsync(request))
                    : (needsSearch
                        ? await scope.DeepSeek.GenerateWebSearchAsync(request, 0)
                        : await scope.DeepSeek.GeneratePitchAsync(request));

                return new ModelCallResult
                {
                    IsSuccess = result.IsSuccess,
                    Content = result.Content ?? "",
                    Error = result.IsSuccess ? null : result.Content,
                    InputTokens = result.PromptTokens,
                    CachedTokens = result.CachedTokens,
                    OutputTokens = result.CompletionTokens,
                    WebSearchCalls = result.WebSearchCalls,
                    SearchEvidence = result.SearchEvidence,
                    OutputItemTypes = result.OutputItemTypes,
                    TokenCost = result.CurrentCost
                };
            }

            return await CallOpenAiAsync(scope, model, prompt, needsSearch, batchCount, cancellationToken);
        }

        /// <summary>
        /// Output budget for one batch.
        ///
        /// ModelRates.MaxTokens is sized for writing a single email, so it is
        /// nowhere near enough for a reply carrying one object per contact —
        /// and going over does not error, it truncates the JSON mid-array and
        /// loses the whole batch. Roughly 220 tokens per contact covers an ID,
        /// a score, a sentence or two of comments and a couple of suggested
        /// corrections, with a fixed allowance on top for the wrapper and any
        /// preamble.
        ///
        /// It was 120 before data integrity began returning corrections. A
        /// correction carries the old value, the new one and the evidence for
        /// it, which is comparable in size to the comments themselves — so a
        /// budget sized for a score and a comment leaves a full batch one
        /// wordy record away from being truncated and thrown out.
        ///
        /// A web search check needs far more than the answer costs. On the
        /// Responses endpoint the model's own reasoning and its running search
        /// commentary are billed against this same ceiling, and they dwarf the
        /// results: a ten contact batch can burn four thousand reasoning tokens
        /// over a dozen search rounds before writing a single result. When the
        /// ceiling runs out mid-research the reply comes back with no results
        /// object at all, which is indistinguishable downstream from a model
        /// that answered in the wrong format.
        /// </summary>
        private static int OutputBudgetFor(int batchCount, bool usesWebSearch) =>
            usesWebSearch
                ? Math.Clamp(batchCount * 800 + 4000, 16000, 64000)
                : Math.Clamp(batchCount * 220 + 1000, 4000, 32000);

        private async Task<ModelCallResult> CallOpenAiAsync(
            ProviderScope scope,
            string model,
            string prompt,
            bool needsSearch,
            int batchCount,
            CancellationToken cancellationToken)
        {
            var rate = await scope.Context.ModelRates.FirstOrDefaultAsync(
                m => m.ModelName == model, cancellationToken);

            var maxTokens = Math.Max(rate?.MaxTokens ?? 0, OutputBudgetFor(batchCount, needsSearch));

            var body = new Dictionary<string, object>
            {
                ["model"] = model,
                ["input"] = prompt,
                ["max_output_tokens"] = maxTokens
            };

            if (needsSearch)
            {
                body["tools"] = new object[] { new { type = "web_search", external_web_access = true } };
                // "required" rather than "auto": a research check that answers
                // from memory is worthless here, and auto lets the model decide
                // it already knows. The zero-search guard in the batch loop is
                // the backstop for a model that ignores this.
                body["tool_choice"] = "required";
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
            var evidence = ExtractSearchEvidence(parsed);
            var itemTypes = ExtractOutputItemTypes(parsed);

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
                    SearchEvidence = evidence,
                    OutputItemTypes = itemTypes,
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
                SearchEvidence = evidence,
                OutputItemTypes = itemTypes,
                TokenCost = tokenCost
            };
        }

        private static int CountWebSearchCalls(JObject parsed)
        {
            if (parsed["output"] is not JArray outputs) return 0;

            return outputs.Count(item =>
                item["type"]?.ToString()?.Contains("web_search", StringComparison.OrdinalIgnoreCase) == true);
        }

        /// <summary>
        /// What was searched for and which pages were opened, one entry per
        /// search action, so a stored score can be traced back to its evidence.
        /// Mirrors the DeepSeek-side extractor; both endpoints put an "action"
        /// on the search item.
        /// </summary>
        /// <summary>Distinct output item types, for telling a skipped search apart from an unrecognised one.</summary>
        private static List<string> ExtractOutputItemTypes(JObject parsed)
        {
            if (parsed["output"] is not JArray outputs) return new List<string>();

            return outputs
                .Select(item => item["type"]?.ToString())
                .Where(type => !string.IsNullOrWhiteSpace(type))
                .Select(type => type!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static List<string> ExtractSearchEvidence(JObject parsed)
        {
            var evidence = new List<string>();

            if (parsed["output"] is not JArray outputs) return evidence;

            foreach (var item in outputs)
            {
                if (item["type"]?.ToString()?.Contains("web_search", StringComparison.OrdinalIgnoreCase) != true)
                    continue;

                var action = item["action"];
                if (action == null) continue;

                var kind = action["type"]?.ToString();
                if (string.IsNullOrWhiteSpace(kind)) kind = "search";

                var query = action["query"]?.ToString();
                var url = action["url"]?.ToString();

                if (!string.IsNullOrWhiteSpace(query))
                    evidence.Add($"{kind}: {query}");
                else if (!string.IsNullOrWhiteSpace(url))
                    evidence.Add($"{kind}: {url}");
                else
                    evidence.Add(kind);
            }

            return evidence;
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
        private static Dictionary<string, ValidationResultItemDto> ParseResults(
            string content,
            string checkType)
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
                    Suggestions = ReadSuggestions(element, checkType),
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

        /// <summary>
        /// Reads the field corrections out of one result object.
        ///
        /// Three things are thrown away rather than shown: a field the Accept
        /// endpoint is not allowed to write, a suggestion with no replacement
        /// value, and one whose replacement equals what the record already
        /// says. The last is the common case — a model listing a field it
        /// checked and left alone — and an Accept button that writes back the
        /// value already there is worse than no button, because the user
        /// cannot tell it did nothing.
        ///
        /// The reason is required. A one-click write to a customer's data has
        /// to be reviewable, and "trust me" is not reviewable; the prompt asks
        /// for the evidence, and a suggestion that arrives without it is a
        /// suggestion we cannot show a user enough about to let them accept.
        /// </summary>
        /// <summary>
        /// The keys a check's corrections can arrive under. The plain
        /// "suggestions" fallback is last so a model that drops the prefix
        /// still gets read.
        /// </summary>
        private static string[] SuggestionKeysFor(string checkType) =>
            ValidationCheckTypes.Normalize(checkType) switch
            {
                ValidationCheckTypes.ContactFit => new[]
                {
                    "Contact Fit suggestions", "contact_fit_suggestions", "suggestions"
                },
                ValidationCheckTypes.LiveContact => new[]
                {
                    "Live Contact suggestions", "live_contact_suggestions",
                    "Live Contact Validity suggestions", "suggestions"
                },
                _ => new[]
                {
                    "Data Integrity suggestions", "data_integrity_suggestions", "suggestions"
                }
            };

        private static List<ValidationSuggestionDto> ReadSuggestions(
            JObject element,
            string checkType)
        {
            var suggestions = new List<ValidationSuggestionDto>();

            var array = SuggestionKeysFor(checkType)
                .Select(key => element.GetValue(key, StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault(token => token != null);

            if (array is not JArray items) return suggestions;

            var index = 0;

            foreach (var item in items.OfType<JObject>())
            {
                var raw = ReadString(item, "field", "field_name", "fieldName");

                // Two gates, not one: the field has to be writable at all, and
                // it has to be writable *by this check*. A live contact prompt
                // naming the email address is the case that matters — the field
                // is real, and it still must not be rewritten from here.
                if (!ValidationSuggestionFields.IsWritableBy(checkType, raw)) continue;

                var field = ValidationSuggestionFields.Normalize(raw)!;

                var suggested = ReadString(item, "suggested", "suggested_value",
                                                 "suggestedValue", "corrected", "correction")?.Trim();

                if (string.IsNullOrWhiteSpace(suggested)) continue;

                var current = ReadString(item, "current", "current_value",
                                               "currentValue", "original")?.Trim();

                if (string.Equals(current, suggested, StringComparison.Ordinal)) continue;

                var reason = ReadString(item, "reason", "evidence", "explanation")?.Trim();

                if (string.IsNullOrWhiteSpace(reason)) continue;

                suggestions.Add(new ValidationSuggestionDto
                {
                    // Position within this result, which is what makes it
                    // stable: the list is replaced wholesale by the next run,
                    // never appended to, so index 2 means the same suggestion
                    // for as long as this result exists.
                    Id = $"s{index++}",
                    Field = field,
                    Current = current,
                    Suggested = suggested,
                    Reason = reason,
                    Status = ValidationSuggestionStatuses.Pending
                });
            }

            return suggestions;
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

                // Written for whichever check ran, and only that one. A re-run
                // replaces its own corrections wholesale, including any already
                // accepted: it has just judged the corrected record and has
                // nothing left to say about it.
                row.SetSuggestionsJson(
                    job.CheckType, SerialiseSuggestions(result.Suggestions, contact));

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
        /// <summary>
        /// Stores this run's suggestions, dropping any whose replacement the
        /// contact already holds.
        /// </summary>
        /// <remarks>
        /// The model is told what the record said when the batch was built, but
        /// it is the contact row as it stands now that Accept would write to,
        /// and the two can differ: another user can edit a contact while a
        /// hundred-contact run is still working through its batches. Comparing
        /// against the live row is what keeps a stale suggestion from offering
        /// to undo an edit made two minutes ago.
        ///
        /// Null rather than "[]" when there is nothing to offer, so a clean
        /// record costs no row width and the UI has one emptiness to test for.
        /// </remarks>
        private static string? SerialiseSuggestions(
            List<ValidationSuggestionDto>? suggestions,
            Contact contact)
        {
            var usable = (suggestions ?? new List<ValidationSuggestionDto>())
                .Where(suggestion => !string.Equals(
                    ValidationSuggestionFields.Read(contact, suggestion.Field)?.Trim(),
                    suggestion.Suggested.Trim(),
                    // Case-sensitive, because a change of case IS the
                    // correction. "Aamir sheikh" to "Aamir Sheikh" is one of
                    // the fixes this check exists to offer, and an
                    // OrdinalIgnoreCase comparison here reads it as a
                    // suggestion the contact already holds and silently drops
                    // it.
                    StringComparison.Ordinal))
                .ToList();

            return usable.Count == 0 ? null : ValidationSuggestionJson.Serialize(usable);
        }

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

            // Loaded once: a discovered address is only written back if no other
            // contact of this client already holds it, and that check needs the
            // client's own contacts rather than every contact in the table.
            var clientFileIds = await _context.data_files
                .AsNoTracking()
                .Where(df => df.client_id == job.ClientId)
                .Select(df => df.id)
                .ToListAsync(cancellationToken);

            var existing = await _context.contact_validations
                .Where(v => v.ClientId == job.ClientId && contactIds.Contains(v.ContactId))
                .ToListAsync(cancellationToken);

            var byContact = existing.ToDictionary(v => v.ContactId);

            if (contacts.Count == 0)
                return;

            // The same shape as the model checks: the lookups run in parallel
            // under the process-wide gate and every database write stays on this
            // thread. Unlike the model path no scope is needed - Prospeo and
            // Hunter hold no database context of their own, only an HttpClient.
            var channel = Channel.CreateUnbounded<EmailOutcome>(
                new UnboundedChannelOptions { SingleReader = true });

            var producer = Task.Run(async () =>
            {
                try
                {
                    await Parallel.ForEachAsync(
                        contacts,
                        new ParallelOptions
                        {
                            MaxDegreeOfParallelism = MaxParallelBatches,
                            CancellationToken = cancellationToken
                        },
                        async (contact, token) =>
                        {
                            await ProviderGate.WaitAsync(token);

                            try
                            {
                                var outcome = await LookUpOneAddressAsync(job, contact, token);
                                await channel.Writer.WriteAsync(outcome, CancellationToken.None);
                            }
                            finally
                            {
                                ProviderGate.Release();
                            }
                        });
                }
                finally
                {
                    channel.Writer.Complete();
                }
            }, cancellationToken);

            var reader = channel.Reader;

            try
            {
                while (true)
                {
                    bool hasMore;

                    using (var idle = new CancellationTokenSource(HeartbeatInterval))
                    using (var linked = CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken, idle.Token))
                    {
                        try
                        {
                            hasMore = await reader.WaitToReadAsync(linked.Token);
                        }
                        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                        {
                            job.HeartbeatAt = DateTime.UtcNow;
                            await _context.SaveChangesAsync(cancellationToken);
                            continue;
                        }
                    }

                    if (!hasMore)
                        break;

                    // Drained and saved together. This used to save once per
                    // contact, which on a list of four hundred was four hundred
                    // round trips to store results the run had already paid for.
                    while (reader.TryRead(out var outcome))
                    {
                        await ApplyEmailOutcomeAsync(
                            job, outcome, itemsByContact, byContact, clientFileIds, cancellationToken);
                    }

                    job.ProcessedCount = items.Count(i => i.Status == ValidationItemStatuses.Completed);
                    job.HeartbeatAt = DateTime.UtcNow;
                    await _context.SaveChangesAsync(cancellationToken);
                }
            }
            catch
            {
                await DrainAsync(producer);
                throw;
            }

            await producer;
        }

        /// <summary>
        /// One address lookup with a single retry, and nothing that touches the
        /// job's database context.
        ///
        /// "Not found" is an answer, not a failure: it is recorded as a low
        /// confidence result and never retried, because asking the same two
        /// providers the same question again produces the same nothing. Only a
        /// lookup that threw or ran out of time gets a second attempt.
        /// </summary>
        private async Task<EmailOutcome> LookUpOneAddressAsync(
            ContactValidationJob job,
            Contact contact,
            CancellationToken cancellationToken)
        {
            string? failure = null;

            for (var attempt = 1; attempt <= 2; attempt++)
            {
                using var lookupTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                lookupTimeout.CancelAfter(ModelCallTimeout);

                try
                {
                    var outcome = await VerifyOneAddressAsync(contact, lookupTimeout.Token);

                    return new EmailOutcome { Contact = contact, Outcome = outcome };
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    failure = $"The address lookup timed out after {ModelCallTimeout.TotalSeconds:0}s.";
                }
                catch (Exception ex)
                {
                    failure = "The address lookup failed: " + ex.Message;
                }

                if (attempt == 2)
                    break;

                _logger.LogWarning(
                    "Validation job {JobId}: the lookup for contact {ContactId} failed ({Reason}). Retrying once.",
                    job.Id, contact.id, Truncate(failure, 200));

                await Task.Delay(RetryDelay, cancellationToken);
            }

            return new EmailOutcome { Contact = contact, Error = failure };
        }

        /// <summary>
        /// Folds one finished lookup into the run. Called only from the run's
        /// own thread - everything it touches is tracked by the job's context.
        /// </summary>
        private async Task ApplyEmailOutcomeAsync(
            ContactValidationJob job,
            EmailOutcome result,
            IReadOnlyDictionary<int, ContactValidationJobItem> itemsByContact,
            Dictionary<int, ContactValidation> byContact,
            List<int> clientFileIds,
            CancellationToken cancellationToken)
        {
            var contact = result.Contact;

            if (!itemsByContact.TryGetValue(contact.id, out var item))
                return;

            // A lookup that never produced an answer is a failure and is now
            // recorded as one. This check could not fail an item at all before,
            // so a provider outage was billed in full and reported as complete.
            if (result.Outcome == null)
            {
                item.Status = ValidationItemStatuses.Failed;
                item.Error = result.Error ?? "The address lookup returned nothing.";
                return;
            }

            var outcome = result.Outcome;
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

            var comments = outcome.Comments;

            // A contact with no address on file gets the discovered one written
            // back. Finding an address and leaving it in a comment where nothing
            // can send to it would waste the lookup, which is the expensive part
            // of this check.
            if (string.IsNullOrWhiteSpace(contact.email) &&
                !string.IsNullOrWhiteSpace(outcome.FoundEmail))
            {
                var (filled, reason) = await FillMissingEmailAsync(
                    contact.id, clientFileIds, outcome.FoundEmail!, cancellationToken);

                comments = filled
                    ? comments + " It has been saved to this contact."
                    : comments + " " + reason;
            }

            row.EmailValidityConfidence = outcome.Confidence;
            row.EmailValidityStatus = outcome.Status;
            row.EmailValiditySource = outcome.Source;
            row.EmailValidityComments = comments;
            row.SetSuggestionsJson(
                ValidationCheckTypes.EmailVerification,
                BuildEmailSuggestion(contact, outcome));
            row.EmailCheckedAt = now;
            row.UpdatedAt = now;

            item.Status = ValidationItemStatuses.Completed;
            item.Error = null;
        }

        /// <summary>One finished address lookup on its way back to the run's thread.</summary>
        private sealed class EmailOutcome
        {
            public Contact Contact { get; init; } = null!;

            /// <summary>Null when the lookup failed outright, even after its retry.</summary>
            public EmailCheckOutcome? Outcome { get; init; }

            public string? Error { get; init; }
        }

        /// <summary>
        /// Offers the provider's address when it differs from the one on file.
        /// </summary>
        /// <remarks>
        /// No model is involved: this is Prospeo's or Hunter's answer, and the
        /// evidence is the provider's own verification. That is also why it is
        /// offered rather than written. A contact with no address gets the
        /// discovered one saved automatically, because there is nothing to lose
        /// — but replacing an address someone may have been mailing for a year
        /// is a decision, and the provider being confident is not the same as
        /// the provider being right.
        ///
        /// Nothing is offered when the addresses match, when the lookup found
        /// nothing, or when the field was empty and has just been filled in.
        /// </remarks>
        private static string? BuildEmailSuggestion(Contact contact, EmailCheckOutcome outcome)
        {
            var stored = contact.email?.Trim();
            var found = outcome.FoundEmail?.Trim();

            if (string.IsNullOrWhiteSpace(stored) || string.IsNullOrWhiteSpace(found))
                return null;

            if (string.Equals(stored, found, StringComparison.OrdinalIgnoreCase))
                return null;

            var provider = outcome.Source switch
            {
                "prospeo" => "Prospeo",
                "hunter" => "Hunter",
                _ => outcome.Source
            };

            var status = string.IsNullOrWhiteSpace(outcome.Status)
                ? ""
                : $" It reports the address as {outcome.Status}.";

            return ValidationSuggestionJson.Serialize(new[]
            {
                new ValidationSuggestionDto
                {
                    Id = "s0",
                    Field = ValidationSuggestionFields.Email,
                    Current = stored,
                    Suggested = found,
                    Reason = $"{provider} returned this address for this person " +
                             $"instead of the one on file.{status}",
                    Status = ValidationSuggestionStatuses.Pending
                }
            });
        }

        /// <summary>
        /// What one lookup established.
        ///
        /// <see cref="FoundEmail"/> is carried separately from the comments so a
        /// contact with an empty address field can be filled in from it: for
        /// those contacts the check is discovery, not verification, and the
        /// address is the result rather than a footnote about it.
        /// </summary>
        private sealed record EmailCheckOutcome(
            int Confidence,
            string? Status,
            string Source,
            string Comments,
            string? FoundEmail);

        private async Task<EmailCheckOutcome> VerifyOneAddressAsync(
            Contact contact,
            CancellationToken cancellationToken)
        {
            var stored = contact.email?.Trim();
            var hasStored = !string.IsNullOrWhiteSpace(stored);

            // Prospeo matches on a LinkedIn profile, so a contact without one
            // goes straight to Hunter rather than spending a lookup that cannot
            // succeed.
            if (!string.IsNullOrWhiteSpace(contact.linkedin_url) && _prospeoService.IsConfigured)
            {
                var prospeo = await _prospeoService.FindEmailAsync(contact.linkedin_url!, cancellationToken);

                if (prospeo.Found)
                {
                    if (!hasStored)
                        return new EmailCheckOutcome(98, prospeo.EmailStatus, "prospeo",
                            $"No address was on file. Prospeo found and verified {prospeo.Email}.",
                            prospeo.Email);

                    var matchesStored = string.Equals(
                        prospeo.Email, stored, StringComparison.OrdinalIgnoreCase);

                    // A verified address that differs from the stored one is not
                    // a pass: the record on file is still the wrong address, and
                    // saying so is the whole value of the check.
                    return matchesStored
                        ? new EmailCheckOutcome(98, prospeo.EmailStatus, "prospeo",
                            "Prospeo verified the address on file.", prospeo.Email)
                        : new EmailCheckOutcome(60, prospeo.EmailStatus, "prospeo",
                            $"Prospeo verified a different address for this person: {prospeo.Email}. The address on file may be out of date.",
                            prospeo.Email);
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
                        EmailHint = stored
                    },
                    cancellationToken);

                if (hunter.Found)
                {
                    if (!hasStored)
                        return new EmailCheckOutcome(hunter.Score, hunter.VerificationStatus, "hunter",
                            $"No address was on file. Hunter found {hunter.Email} with a confidence of {hunter.Score}.",
                            hunter.Email);

                    var matchesStored = string.Equals(
                        hunter.Email, stored, StringComparison.OrdinalIgnoreCase);

                    return matchesStored
                        ? new EmailCheckOutcome(hunter.Score, hunter.VerificationStatus, "hunter",
                            $"Hunter confirmed the address on file with a confidence of {hunter.Score}.",
                            hunter.Email)
                        : new EmailCheckOutcome(Math.Min(hunter.Score, 60), hunter.VerificationStatus, "hunter",
                            $"Hunter found a different address for this person: {hunter.Email}. The address on file may be out of date.",
                            hunter.Email);
                }

                return new EmailCheckOutcome(10, null, "hunter",
                    hunter.RejectedBecause ??
                        (hasStored
                            ? "Neither provider could confirm an address for this contact."
                            : "No address is on file and neither provider could find one."),
                    null);
            }

            return new EmailCheckOutcome(0, null, "none",
                "No email verification provider is configured. An admin needs to add a Prospeo or Hunter API key.",
                null);
        }

        /// <summary>
        /// Writes a discovered address onto a contact that had none.
        ///
        /// Refuses if another of the client's contacts already holds it: the
        /// whole product keys on address uniqueness per client, and a lookup
        /// that quietly created a duplicate would be worse than one that found
        /// nothing. The contact is re-read tracked because the run loads its
        /// contacts read-only.
        /// </summary>
        private async Task<(bool Filled, string Reason)> FillMissingEmailAsync(
            int contactId,
            List<int> clientFileIds,
            string discovered,
            CancellationToken cancellationToken)
        {
            var email = discovered.Trim();

            var duplicate = await _context.contacts
                .AsNoTracking()
                .AnyAsync(c => c.id != contactId &&
                               c.email != null &&
                               c.email.ToLower() == email.ToLower() &&
                               c.DataFileId.HasValue &&
                               clientFileIds.Contains(c.DataFileId.Value),
                    cancellationToken);

            if (duplicate)
                return (false, "It was not saved: another contact already holds this address.");

            var tracked = await _context.contacts
                .FirstOrDefaultAsync(c => c.id == contactId, cancellationToken);

            if (tracked == null)
                return (false, "It could not be saved: the contact no longer exists.");

            // Re-checked on the tracked row in case something else filled the
            // field while the lookup was in flight.
            if (!string.IsNullOrWhiteSpace(tracked.email))
                return (false, "It was not saved: an address was added to this contact meanwhile.");

            tracked.email = email;
            tracked.updated_at = DateTime.UtcNow;

            return (true, "");
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
