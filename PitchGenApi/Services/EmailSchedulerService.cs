using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PitchGenApi.Database;
using PitchGenApi.Interfaces;
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

                foreach (var group in groupedSteps)
                {
                    Console.WriteLine($"🧩 Processing group scheduled at: {group.Key}");

                    var tasks = group.Select(async step =>
                    {
                        try
                        {
                            Console.WriteLine($"➡️ Starting step ID: {step.Id}");

                            var contactRepo = scope.ServiceProvider
                                .GetRequiredService<ContactRepository>();

                            var domainRepo = scope.ServiceProvider
                                .GetRequiredService<IDomainVerificationRepository>();

                            var helper = new ScheduledEmailSendingHelper(
                                scope.ServiceProvider,
                                contactRepo,
                                domainRepo
                            );

                            await helper.ProcessStepAsync(step, stoppingToken);

                            Console.WriteLine($"✅ Finished step ID: {step.Id}");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"❌ Error in step ID {step.Id}: {ex.Message}");
                        }
                    });

                    await Task.WhenAll(tasks);
                }

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
