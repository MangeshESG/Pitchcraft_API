//using Microsoft.EntityFrameworkCore;
//using PitchGenApi.Database;
//using System;
//using System.Linq;
//using System.Threading.Tasks;

//public class MonthlyCreditResetJob
//{
//    private readonly AppDbContext _context;

//    public MonthlyCreditResetJob(AppDbContext context)
//    {
//        _context = context;
//    }

//    public async Task Execute()
//    {
//        Console.WriteLine($"🕐 [{DateTime.Now}] MonthlyCreditResetJob started...");

//        // ✅ Step 1 — Mark expired plans
//        var justExpiredPlans = await _context.UserCredits
//            .Where(u => u.Status.ToLower() == "active" && u.EndDate <= DateTime.UtcNow)
//            .ToListAsync();

//        foreach (var plan in justExpiredPlans)
//            plan.Status = "expired";

//        if (justExpiredPlans.Any())
//            Console.WriteLine($"⚠️ Marked {justExpiredPlans.Count} plans as expired.");

//        // ✅ Step 2 — Mark Custom Credits as "Used" if Credits == 0
//        var customCredits = await _context.UserCredits
//            .Where(c => c.Plane == "Custom Credit" && c.Credits == 0)
//            .ToListAsync();

//        foreach (var plan in customCredits)
//            plan.Status = "Used";

//        // ✅ Step 3 — Handle clients whose latest plan expired (only affect TotalCredit)
//        var clientsWithLatestExpiredPlan = justExpiredPlans
//            .GroupBy(p => p.ClientId)
//            .Select(g => g.OrderByDescending(x => x.EndDate).First())
//            .Select(x => x.ClientId)
//            .Distinct()
//            .ToList();

//        foreach (var clientId in clientsWithLatestExpiredPlan)
//        {
//            var latestExpiredPlan = justExpiredPlans
//                .Where(p => p.ClientId == clientId)
//                .OrderByDescending(p => p.EndDate)
//                .FirstOrDefault();

//            if (latestExpiredPlan == null)
//                continue;

//            // 🔹 Get FinalUserCredit for this client
//            var finalCredit = await _context.FinalUserCredit
//                .FirstOrDefaultAsync(f => f.ClientId == clientId);

//            if (finalCredit != null)
//            {
//                var beforePlanCredit = finalCredit.TotalCredit ?? 0;

//                // ✅ Only reset plan credits; CustomCredit remains as is
//                finalCredit.TotalCredit = 0;
//                finalCredit.MonthlyLimit = 0;
//                finalCredit.UpdatedAt = DateTime.UtcNow;

//                Console.WriteLine($"📉 ClientId {clientId}: Plan expired → PlanCredit {beforePlanCredit} → {finalCredit.TotalCredit} | CustomCredit left = {finalCredit.CustomLimit}");
//            }

//            // Mark all expired non-custom plans for this client
//            var expiredPlans = justExpiredPlans
//                .Where(p => p.ClientId == clientId && p.Plane != "Custom Credit")
//                .ToList();

//            foreach (var plan in expiredPlans)
//                plan.Status = "expired";
//        }

//        // ✅ Step 4 — Reset monthly limits if ResetDate passed
//        var plansToReset = await _context.UserCredits
//            .Where(u => u.Status.ToLower() == "active" && u.ResetDate <= DateTime.UtcNow)
//            .ToListAsync();

//        foreach (var plan in plansToReset)
//            plan.ResetDate = (plan.ResetDate ?? DateTime.UtcNow).AddMonths(1);

//        if (plansToReset.Any())
//            Console.WriteLine($"🔄 Found {plansToReset.Count} active plans to reset monthly limit.");

//        // ✅ Step 5 — Reset LimitUsed in FinalUserCredit
//        var clientIdsToReset = plansToReset.Select(p => p.ClientId).Distinct().ToList();

//        if (clientIdsToReset.Count > 0)
//        {
//            var usersCredits = await _context.FinalUserCredit
//                .Where(f => clientIdsToReset.Contains(f.ClientId))
//                .ToListAsync();

//            foreach (var credit in usersCredits)
//            {
//                credit.LimitUsed = 0;
//                credit.UpdatedAt = DateTime.UtcNow;
//            }

//            if (usersCredits.Any())
//                Console.WriteLine($"✅ Reset monthly LimitUsed for {usersCredits.Count} users.");
//        }

//        // ✅ Step 6 — Save all updates
//        await _context.SaveChangesAsync();

//        Console.WriteLine($"🎯 [{DateTime.Now}] MonthlyCreditResetJob completed successfully.\n");
//    }
//}
