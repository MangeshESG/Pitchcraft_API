using Microsoft.EntityFrameworkCore;
using PitchGenApi.Database;
using PitchGenApi.Interfaces;
using PitchGenApi.Model;
using PitchGenApi.Services;
using Stripe.Terminal;

public class BackgroundWorkerService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;

    public BackgroundWorkerService(IServiceProvider serviceProvider, IConfiguration configuration)
    {
        _serviceProvider = serviceProvider;
        _configuration = configuration;
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
            return Task.CompletedTask;
        }

        var loops = new List<Task>();

        if (backgroundJobsEnabled)
        {
            Console.WriteLine("✅ Background jobs enabled.");

            loops.Add(RunEmailScheduler(stoppingToken));
            loops.Add(RunMonthlyCreditReset(stoppingToken));
            loops.Add(RunInboxEmailSync(stoppingToken));
            loops.Add(RunGmailInboxSync(stoppingToken));
            loops.Add(RunOutlookInboxSync(stoppingToken));
        }
        else
        {
            Console.WriteLine(
                "⚠️ Background jobs are disabled; only the validation runner is on.");
        }

        if (validationRunnerEnabled)
        {
            loops.Add(RunValidationJobs(stoppingToken));
        }

        return Task.WhenAll(loops);
    }

    /// <summary>
    /// Drains queued Audience Assurance runs.
    ///
    /// A hundred contacts with web search enabled takes minutes, which is why
    /// the API queues rather than executes: the request returns a job id at
    /// once and this picks the work up. Concurrency is capped low on purpose —
    /// each job is already a batched, long-running call to a rate-limited
    /// provider, so running many at once buys nothing and risks throttling.
    /// </summary>
    private async Task RunValidationJobs(CancellationToken stoppingToken)
    {
        Console.WriteLine("✅ ValidationJobRunner started...");

        const int maxParallel = 3;
        using var semaphore = new SemaphoreSlim(maxParallel);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                var queuedIds = await context.contact_validation_jobs
                    .Where(j => j.Status == ValidationJobStatuses.Queued)
                    .OrderBy(j => j.CreatedAt)
                    .Take(maxParallel * 2)
                    .Select(j => j.Id)
                    .ToListAsync(stoppingToken);

                if (queuedIds.Count > 0)
                {
                    Console.WriteLine($"🔎 {queuedIds.Count} validation run(s) queued.");

                    var tasks = queuedIds.Select(async jobId =>
                    {
                        await semaphore.WaitAsync(stoppingToken);

                        try
                        {
                            // A scope per job: the runs are long, and sharing one
                            // DbContext across them would let a slow job hold the
                            // change tracker for every other.
                            using var innerScope = _serviceProvider.CreateScope();

                            var service = innerScope.ServiceProvider
                                .GetRequiredService<IContactValidationService>();

                            await service.ProcessJobAsync(jobId, stoppingToken);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"❌ Validation job {jobId} failed: {ex.Message}");
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    });

                    await Task.WhenAll(tasks);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"🔥 Error in ValidationJobRunner: {ex.Message}");
            }

            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
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

