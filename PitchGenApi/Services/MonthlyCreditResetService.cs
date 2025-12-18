//using Microsoft.Extensions.DependencyInjection;
//using Microsoft.Extensions.Hosting;
//using System;
//using System.Threading;
//using System.Threading.Tasks;
//using PitchGenApi.Database; // ✅ Make sure to import your DbContext

//public class MonthlyCreditResetService : BackgroundService
//{
//    private readonly IServiceProvider _serviceProvider;

//    public MonthlyCreditResetService(IServiceProvider serviceProvider)
//    {
//        _serviceProvider = serviceProvider;
//    }

//    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
//    {
//        Console.WriteLine("✅ MonthlyCreditResetService started...");

//        while (!stoppingToken.IsCancellationRequested)
//        {
//            try
//            {
//                using var scope = _serviceProvider.CreateScope();

//                var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
//                var job = new MonthlyCreditResetJob(context);

//                await job.Execute(); // 🔹 Run your existing job

//                Console.WriteLine("⏳ Waiting 1 minute before next run...");
//            }
//            catch (Exception ex)
//            {
//                Console.WriteLine($"❌ Error in MonthlyCreditResetService: {ex.Message}");
//            }

//            // 🔁 Run every 1 minute
//            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
//        }

//        Console.WriteLine("🛑 MonthlyCreditResetService stopped.");
//    }
//}
