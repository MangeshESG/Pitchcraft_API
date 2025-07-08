using System.Linq;
using Microsoft.EntityFrameworkCore;
using PitchGenApi.Database;
using PitchGenApi.Services;

public class EmailSchedulerService : BackgroundService
{
    //private readonly ILogger<EmailSchedulerService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly ZohoService _zohoService;

    public EmailSchedulerService(
        // ILogger<EmailSchedulerService> logger,
        IServiceProvider serviceProvider,
        ZohoService zohoService)
    {
        //_logger = logger;
        _serviceProvider = serviceProvider;
        _zohoService = zohoService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                var dueSteps = await context.SequenceSteps
                    .Where(s => !s.IsSent)
                    .ToListAsync(stoppingToken);

                var groupedSteps = dueSteps
                    .GroupBy(s => s.ScheduledDate + s.ScheduledTime);

                var groupTasks = groupedSteps.Select(async group =>
                {
                    var innerTasks = group.Select(async step =>
                    {
                        try
                        {
                            var helper = new ScheduledEmailSendingHelper(_serviceProvider, _zohoService);
                            await helper.ProcessStepAsync(step, stoppingToken);
                        }
                        catch (Exception ex)
                        {
                        }
                    });

                    await Task.WhenAll(innerTasks);
                });

                await Task.WhenAll(groupTasks);
            }
            catch (Exception ex)
            {
            }

            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        }
    }
}
