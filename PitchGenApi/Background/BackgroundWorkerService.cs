using Microsoft.EntityFrameworkCore;
using PitchGenApi.Database;
using PitchGenApi.Interfaces;
using PitchGenApi.Services;

public class BackgroundWorkerService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<BackgroundWorkerService> _logger;

    public BackgroundWorkerService(IServiceProvider serviceProvider, ILogger<BackgroundWorkerService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        return Task.WhenAll(
            RunEmailScheduler(stoppingToken),
            RunMonthlyCreditReset(stoppingToken),
            RunInboxEmailSync(stoppingToken)
        );
    }

    private async Task RunEmailScheduler(CancellationToken stoppingToken)
    {
        _logger.LogInformation("EmailScheduler started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _logger.LogInformation("EmailScheduler: Checking for pending steps");

                using var scope = _serviceProvider.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                var dueSteps = await context.SequenceSteps
                    .Where(s => !s.IsSent)
                    .ToListAsync(stoppingToken);

                _logger.LogInformation("EmailScheduler: Found {Count} pending step(s)", dueSteps.Count);

                var groupedSteps = dueSteps.GroupBy(s => s.ScheduledDate + s.ScheduledTime);

                foreach (var group in groupedSteps)
                {
                    var tasks = group.Select(async step =>
                    {
                        try
                        {
                            _logger.LogInformation("EmailScheduler: Processing step ID {StepId}", step.Id);

                            var contactRepo = scope.ServiceProvider.GetRequiredService<ContactRepository>();
                            var domainRepo = scope.ServiceProvider.GetRequiredService<IDomainVerificationRepository>();
                            var helper = new ScheduledEmailSendingHelper(scope.ServiceProvider, contactRepo, domainRepo);
                            await helper.ProcessStepAsync(step, stoppingToken);

                            _logger.LogInformation("EmailScheduler: Step ID {StepId} processed successfully", step.Id);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "EmailScheduler: Failed on step ID {StepId}", step.Id);
                        }
                    });

                    await Task.WhenAll(tasks);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "EmailScheduler: Fatal error in scheduler loop");
            }

            _logger.LogInformation("EmailScheduler: Sleeping 20 seconds");
            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        }

        _logger.LogInformation("EmailScheduler: Stopped");
    }

    private async Task RunMonthlyCreditReset(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MonthlyCreditReset: Started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _logger.LogInformation("MonthlyCreditReset: Running job");

                using var scope = _serviceProvider.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var job = new MonthlyCreditResetJob(context);
                await job.Execute();

                _logger.LogInformation("MonthlyCreditReset: Job completed successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MonthlyCreditReset: Job failed");
            }

            _logger.LogInformation("MonthlyCreditReset: Sleeping 1 minute");
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }

        _logger.LogInformation("MonthlyCreditReset: Stopped");
    }

    private async Task RunInboxEmailSync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("InboxEmailSync: Started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _logger.LogInformation("InboxEmailSync: Loading inbox credentials");

                using var scope = _serviceProvider.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                var users = await context.Inboxcredentials.ToListAsync(stoppingToken);
                _logger.LogInformation("InboxEmailSync: Found {UserCount} inbox account(s) to sync", users.Count);

                var batches = users.Chunk(5);

                foreach (var batch in batches)
                {
                    var tasks = batch.Select(async user =>
                    {
                        try
                        {
                            _logger.LogInformation("InboxEmailSync: Starting sync for {Username}", user.Username);

                            using var innerScope = _serviceProvider.CreateScope();
                            var syncService = innerScope.ServiceProvider
                                .GetRequiredService<IInboxEmailSyncService>();

                            await syncService.SyncEmailsAsync(user);

                            _logger.LogInformation("InboxEmailSync: Completed sync for {Username}", user.Username);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "InboxEmailSync: Sync failed for {Username}", user.Username);
                        }
                    });

                    await Task.WhenAll(tasks);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "InboxEmailSync: Outer loop error");
            }

            _logger.LogInformation("InboxEmailSync: Sleeping 5 minutes");
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }

        _logger.LogInformation("InboxEmailSync: Stopped");
    }
}
