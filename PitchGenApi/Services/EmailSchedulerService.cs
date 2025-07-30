using System.Linq;
using Microsoft.EntityFrameworkCore;
using PitchGenApi.Database;
using PitchGenApi.Services;

public class EmailSchedulerService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;

    public EmailSchedulerService(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Console.WriteLine("✅ EmailSchedulerService started...");

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

                var groupedSteps = dueSteps
                    .GroupBy(s => s.ScheduledDate + s.ScheduledTime);

                var groupTasks = groupedSteps.Select(async group =>
                {
                    Console.WriteLine($"🧩 Processing group scheduled at: {group.Key}");

                    var innerTasks = group.Select(async step =>
                    {
                        try
                        {
                            Console.WriteLine($"➡️  Starting step ID: {step.Id}");

                            var contactRepo = scope.ServiceProvider.GetRequiredService<ContactRepository>();
                            var helper = new ScheduledEmailSendingHelper(_serviceProvider, contactRepo);

                            await helper.ProcessStepAsync(step, stoppingToken);

                            Console.WriteLine($"✅ Finished step ID: {step.Id}");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"❌ Error in step ID: {step.Id} - {ex.Message}");
                        }
                    });

                    await Task.WhenAll(innerTasks);
                });

                await Task.WhenAll(groupTasks);

                Console.WriteLine("⏳ Waiting 20 seconds for next cycle...");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"🔥 Fatal error in scheduler loop: {ex.Message}");
            }

            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        }

        Console.WriteLine("🛑 EmailSchedulerService stopped.");
    }
}
