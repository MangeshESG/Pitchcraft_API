using Microsoft.EntityFrameworkCore;
using PitchGenApi.Database;
using PitchGenApi.Interfaces;
using PitchGenApi.Services;
using Stripe.Terminal;

public class BackgroundWorkerService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;

    public BackgroundWorkerService(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        return Task.WhenAll(
            RunEmailScheduler(stoppingToken),
            RunMonthlyCreditReset(stoppingToken),
            RunInboxEmailSync(stoppingToken),
            RunGmailInboxSync(stoppingToken)
        );
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
                    .Where(s => !s.IsSent)
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
}

