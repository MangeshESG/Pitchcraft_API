using Microsoft.EntityFrameworkCore;
using PitchGenApi.Background;
using PitchGenApi.Database;
using PitchGenApi.Interfaces;
using PitchGenApi.Model;
using PitchGenApi.Services;
using Stripe.Terminal;

public class BackgroundWorkerService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private readonly ValidationRunnerDiagnostics _diagnostics;

    public BackgroundWorkerService(
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        ValidationRunnerDiagnostics diagnostics)
    {
        _serviceProvider = serviceProvider;
        _configuration = configuration;
        _diagnostics = diagnostics;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var backgroundJobsEnabled =
            _configuration.GetValue<bool>("BackgroundJobs:Enabled");

        // The validation runner can be switched on by itself. The master flag
        // also starts the email scheduler and the inbox syncs, which send real
        // mail and touch real mailboxes — so a developer who only wants to test
        // a validation run should not have to accept that to get it.
        var validationRunnerEnabled =
            backgroundJobsEnabled ||
            _configuration.GetValue<bool>("Validation:RunnerEnabled");

        if (!backgroundJobsEnabled && !validationRunnerEnabled)
        {
            Console.WriteLine("⚠️ Background jobs are disabled from appsettings.json");
            _diagnostics.MarkDisabled(
                "Both BackgroundJobs:Enabled and Validation:RunnerEnabled are false in the configuration this process loaded.");
            return Task.CompletedTask;
        }

        var loops = new List<Task>();

        if (backgroundJobsEnabled)
        {
            Console.WriteLine("✅ Background jobs enabled.");

            loops.Add(Supervise("EmailScheduler", RunEmailScheduler, stoppingToken));
            loops.Add(Supervise("MonthlyCreditReset", RunMonthlyCreditReset, stoppingToken));
            loops.Add(Supervise("InboxEmailSync", RunInboxEmailSync, stoppingToken));
            loops.Add(Supervise("GmailInboxSync", RunGmailInboxSync, stoppingToken));
            loops.Add(Supervise("OutlookInboxSync", RunOutlookInboxSync, stoppingToken));
        }
        else
        {
            Console.WriteLine(
                "⚠️ Background jobs are disabled; only the validation runner is on.");
        }

        if (validationRunnerEnabled)
        {
            _diagnostics.MarkStarted();

            loops.Add(Supervise("ValidationJobRunner", RunValidationJobs, stoppingToken));
            loops.Add(Supervise("ValidationJobReaper", RunValidationReaper, stoppingToken));
        }

        return Task.WhenAll(loops);
    }

    /// <summary>
    /// Keeps one loop alive for the life of the process.
    ///
    /// Two things made the old shape fragile. A loop that threw past its own
    /// try/catch simply ended, and nothing ever started it again — the process
    /// stayed up serving HTTP while its queue quietly stopped draining, which
    /// is exactly what left eight runs sitting at "queued". And because every
    /// loop was handed straight to Task.WhenAll, one unhandled exception would
    /// fault ExecuteAsync, which under .NET's default
    /// BackgroundServiceExceptionBehavior.StopHost takes the whole API down
    /// with it.
    ///
    /// So: nothing escapes here, and a crashed loop is restarted after a short
    /// pause rather than lost.
    /// </summary>
    private async Task Supervise(
        string name,
        Func<CancellationToken, Task> loop,
        CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await loop(stoppingToken);

                // A clean return means the loop saw cancellation; nothing to
                // restart.
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _diagnostics.MarkError(ex);
                _diagnostics.MarkRestart();

                Console.WriteLine($"🔥 {name} crashed and will restart in 10s: {ex}");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Drains queued Audience Assurance runs.
    ///
    /// A hundred contacts with web search enabled takes minutes, which is why
    /// the API queues rather than executes: the request returns a job id at
    /// once and this picks the work up. Concurrency is capped low on purpose —
    /// each job is already a batched, long-running call to a rate-limited
    /// provider, so running many at once buys nothing and risks throttling.
    ///
    /// Dispatch is continuous rather than wave-based: a slot is refilled the
    /// moment the job in it finishes, instead of the whole batch of claimed
    /// jobs being awaited together. The previous Take+WhenAll shape meant one
    /// slow job held up every other slot in its wave, and the poll loop could
    /// not even look for new work until the whole wave finished — which is
    /// how one hung run left every client's job sitting at "queued".
    ///
    /// Jobs are claimed with <see cref="IContactValidationService.ClaimQueuedJobsAsync"/>,
    /// an atomic UPDATE rather than a read-then-write, so a job can never be
    /// reported "queued" while this runner already owns it, and can never be
    /// claimed twice.
    /// </summary>
    private async Task RunValidationJobs(CancellationToken stoppingToken)
    {
        Console.WriteLine("✅ ValidationJobRunner started...");

        const int maxParallel = 3;
        var owner = $"{Environment.MachineName}:{Environment.ProcessId}";
        var inFlight = new Dictionary<int, Task>();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var finishedId in inFlight
                    .Where(kv => kv.Value.IsCompleted)
                    .Select(kv => kv.Key)
                    .ToList())
                {
                    inFlight.Remove(finishedId);
                }

                _diagnostics.MarkPoll(inFlight.Count);

                var freeSlots = maxParallel - inFlight.Count;

                if (freeSlots > 0)
                {
                    using var scope = _serviceProvider.CreateScope();
                    var claimService = scope.ServiceProvider.GetRequiredService<IContactValidationService>();

                    var claimedIds = await claimService.ClaimQueuedJobsAsync(freeSlots, owner, stoppingToken);

                    if (claimedIds.Count > 0)
                    {
                        _diagnostics.MarkClaimed(claimedIds.Count);
                        Console.WriteLine($"🔎 Claimed {claimedIds.Count} validation run(s).");
                    }

                    foreach (var jobId in claimedIds)
                    {
                        inFlight[jobId] = RunOneJobAsync(jobId, stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Logged in full rather than by message alone: when the claim
                // fails every cycle, the message on its own is rarely enough to
                // say why.
                _diagnostics.MarkError(ex);
                Console.WriteLine($"🔥 Error in ValidationJobRunner: {ex}");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        // Give in-flight jobs a chance to reach their own finally blocks
        // (which save with CancellationToken.None) before the host tears
        // down further.
        try
        {
            await Task.WhenAll(inFlight.Values);
        }
        catch
        {
            // Individual job failures are already logged inside RunOneJobAsync.
        }
    }

    private async Task RunOneJobAsync(int jobId, CancellationToken stoppingToken)
    {
        try
        {
            // A scope per job: the runs are long, and sharing one DbContext
            // across them would let a slow job hold the change tracker for
            // every other.
            using var innerScope = _serviceProvider.CreateScope();

            var service = innerScope.ServiceProvider
                .GetRequiredService<IContactValidationService>();

            await service.ProcessJobAsync(jobId, stoppingToken);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Validation job {jobId} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Recovers runs a dead process left stuck at "running". Separate from
    /// the dispatch loop and on a slower cadence — this is a safety net for
    /// abandoned jobs, not part of the normal claim/run path.
    /// </summary>
    private async Task RunValidationReaper(CancellationToken stoppingToken)
    {
        Console.WriteLine("✅ ValidationJobReaper started...");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<IContactValidationService>();

                var recovered = await service.ReapStaleJobsAsync(stoppingToken);

                _diagnostics.MarkReap();

                if (recovered > 0)
                {
                    Console.WriteLine($"🩹 Reaper recovered {recovered} stale validation job(s).");
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _diagnostics.MarkError(ex);
                Console.WriteLine($"🔥 Error in ValidationJobReaper: {ex}");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RunEmailScheduler(CancellationToken stoppingToken)
    {
        Console.WriteLine("✅ EmailScheduler started...");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Console.WriteLine("🔄 Checking for pending steps...");

                using var scope = _serviceProvider.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                var dueSteps = await context.SequenceSteps
                    .Where(s => !s.TestIsSent)
                    .ToListAsync(stoppingToken);

                Console.WriteLine($"🟡 Found {dueSteps.Count} pending step(s).");

                var groupedSteps = dueSteps.GroupBy(s => s.ScheduledDate + s.ScheduledTime);

                foreach (var group in groupedSteps)
                {
                    var tasks = group.Select(async step =>
                    {
                        try
                        {
                            var contactRepo = scope.ServiceProvider.GetRequiredService<ContactRepository>();
                            var domainRepo = scope.ServiceProvider.GetRequiredService<IDomainVerificationRepository>();
                            var helper = new ScheduledEmailSendingHelper(scope.ServiceProvider, contactRepo, domainRepo);
                            await helper.ProcessStepAsync(step, stoppingToken);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"❌ Error in step ID {step.Id}: {ex.Message}");
                        }
                    });

                    await Task.WhenAll(tasks);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"🔥 Fatal error in email scheduler: {ex.Message}");
            }

            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        }
    }

    private async Task RunMonthlyCreditReset(CancellationToken stoppingToken)
    {
        Console.WriteLine("✅ MonthlyCreditReset started...");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var job = new MonthlyCreditResetJob(context);
                await job.Execute();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error in MonthlyCreditReset: {ex.Message}");
            }

            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }

    private async Task RunInboxEmailSync(CancellationToken stoppingToken)
    {
        Console.WriteLine("✅ InboxEmailSync started...");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                var users = await context.Inboxcredentials.ToListAsync(stoppingToken);


                var batches = users.Chunk(5);

                foreach (var batch in batches)
                {
                    var tasks = batch.Select(async user =>
                    {
                        try
                        {
                            // 🔥 NEW SCOPE PER TASK
                            using var innerScope = _serviceProvider.CreateScope();

                            var syncService = innerScope.ServiceProvider
                                .GetRequiredService<IInboxEmailSyncService>();

                            await syncService.SyncEmailsAsync(user);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"❌ InboxSync failed for {user.Username}: {ex.Message}");
                        }
                    });

                    await Task.WhenAll(tasks);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error in InboxEmailSync: {ex.Message}");
            }

            Console.WriteLine("🔁 InboxEmailSync sleeping 5 min...");
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }

    private async Task RunGmailInboxSync(CancellationToken stoppingToken)
    {
        Console.WriteLine("✅ Gmail Inbox Sync started...");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                var tokens = await context.EmailOAuthTokens
                    .Where(x => x.Provider == "Gmail")
                    .ToListAsync(stoppingToken);

                Console.WriteLine($"📧 Total Gmail Accounts: {tokens.Count}");

                // 🔥 PARALLEL LIMIT = 10
                int maxParallel = 10;

                using var semaphore = new SemaphoreSlim(maxParallel);

                var tasks = tokens.Select(async token =>
                {
                    await semaphore.WaitAsync(stoppingToken);

                    try
                    {
                        Console.WriteLine($"🚀 Sync Start: {token.Email}");

                        using var innerScope = _serviceProvider.CreateScope();

                        var gmailService = innerScope.ServiceProvider
                            .GetRequiredService<IInboxEmailSyncService>();

                        await gmailService.SyncGmailInboxAsync(token);

                        Console.WriteLine($"✅ Done: {token.Email}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"❌ Failed: {token.Email} → {ex.Message}");
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                await Task.WhenAll(tasks);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"🔥 Fatal Error: {ex.Message}");
            }

            Console.WriteLine("🔁 Gmail Sync sleeping 2 min...");
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
        }
    }

    private async Task RunOutlookInboxSync(CancellationToken stoppingToken)
    {
        Console.WriteLine("✅ Gmail Inbox Sync started...");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                var tokens = await context.EmailOAuthTokens
                    .Where(x => x.Provider == "Outlook")
                    .ToListAsync(stoppingToken);

                Console.WriteLine($"📧 Total Outlook Accounts: {tokens.Count}");

                // 🔥 PARALLEL LIMIT = 10
                int maxParallel = 10;

                using var semaphore = new SemaphoreSlim(maxParallel);

                var tasks = tokens.Select(async token =>
                {
                    await semaphore.WaitAsync(stoppingToken);

                    try
                    {
                        Console.WriteLine($"🚀 Sync Start: {token.Email}");

                        using var innerScope = _serviceProvider.CreateScope();

                        var gmailService = innerScope.ServiceProvider
                            .GetRequiredService<IInboxEmailSyncService>();

                        await gmailService.SyncOutlookInboxAsync(token);

                        Console.WriteLine($"✅ Done: {token.Email}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"❌ Failed: {token.Email} → {ex.Message}");
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                await Task.WhenAll(tasks);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"🔥 Fatal Error: {ex.Message}");
            }

            Console.WriteLine("🔁 Outlook Sync sleeping 2 min...");
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
        }
    }
}

