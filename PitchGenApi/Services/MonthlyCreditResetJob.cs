using Microsoft.EntityFrameworkCore;
using PitchGenApi.Database;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

public class MonthlyCreditResetJob
{
    private readonly AppDbContext _context;

    public MonthlyCreditResetJob(AppDbContext context)
    {
        _context = context;
    }

    // ✅ Plan hierarchy (small → big)
    private static readonly Dictionary<string, int> PlanPriority = new()
    {
        { "Standard", 1 },
        { "Premium", 2 },
        { "Standard Yearly", 3 },
        { "Premium Yearly", 4 }
    };

    public async Task Execute()
    {
        Console.WriteLine($"🕐 [{DateTime.Now}] MonthlyCreditResetJob started...");

        // ✅ Step 1 — Mark expired plans
        var justExpiredPlans = await _context.UserCredits
            .Where(u => u.Status.ToLower() == "active" && u.EndDate <= DateTime.UtcNow)
            .ToListAsync();

        foreach (var plan in justExpiredPlans)
            plan.Status = "expired";

        if (justExpiredPlans.Any())
            Console.WriteLine($"⚠️ Marked {justExpiredPlans.Count} plans as expired.");

        // ✅ Step 2 — Mark Custom Credits as "Used" if Credits == 0
        var customCredits = await _context.UserCredits
            .Where(c => c.Plane == "Custom Credit" && c.Credits == 0)
            .ToListAsync();

        foreach (var plan in customCredits)
            plan.Status = "Used";

        // ✅ Step 3 — Handle expired plans with hierarchy check
        // ✅ Step 3 — Handle expired plans with hierarchy check (EF safe)
        var clientsWithExpiredPlans = justExpiredPlans
            .Select(p => p.ClientId)
            .Distinct()
            .ToList();

        foreach (var clientId in clientsWithExpiredPlans)
        {
            var expiredPlans = justExpiredPlans
                .Where(p => p.ClientId == clientId && p.Plane != "Custom Credit")
                .ToList();

            if (!expiredPlans.Any())
                continue;

            // 🔹 Highest expired plan priority (in-memory)
            var maxExpiredPriority = expiredPlans
                .Max(p => PlanPriority.GetValueOrDefault(p.Plane, 0));

            // 🔹 Get all ACTIVE plans for client (DB call only)
            var activePlans = await _context.UserCredits
                .Where(p =>
                    p.ClientId == clientId &&
                    p.Status.ToLower() == "active" &&
                    p.Plane != "Custom Credit")
                .ToListAsync();

            // 🔹 Check higher plan in-memory (NO EF translation issue)
            var hasHigherActivePlan = activePlans.Any(p =>
                PlanPriority.GetValueOrDefault(p.Plane, 0) > maxExpiredPriority);

            // 🔹 Mark expired plans
            foreach (var plan in expiredPlans)
                plan.Status = "expired";

            // ❌ Higher plan active → credit untouched
            if (hasHigherActivePlan)
            {
                Console.WriteLine(
                    $"ℹ️ ClientId {clientId}: Higher plan active → credit unchanged.");
                continue;
            }

            // ✅ No higher plan → reset FinalUserCredit
            var finalCredit = await _context.FinalUserCredit
                .FirstOrDefaultAsync(f => f.ClientId == clientId);

            if (finalCredit != null)
            {
                var beforeCredit = finalCredit.TotalCredit ?? 0;

                finalCredit.TotalCredit = 0;
                finalCredit.MonthlyLimit = 0;
                finalCredit.UpdatedAt = DateTime.UtcNow;

                Console.WriteLine(
                    $"📉 ClientId {clientId}: No higher plan → Credit {beforeCredit} → 0");
            }
        }


        // ✅ Step 4 — Reset monthly limits if ResetDate passed
        var plansToReset = await _context.UserCredits
            .Where(u => u.Status.ToLower() == "active" && u.ResetDate <= DateTime.UtcNow)
            .ToListAsync();

        foreach (var plan in plansToReset)
            plan.ResetDate = (plan.ResetDate ?? DateTime.UtcNow).AddMonths(1);

        if (plansToReset.Any())
            Console.WriteLine($"🔄 Found {plansToReset.Count} active plans to reset monthly limit.");

        // ✅ Step 5 — Reset LimitUsed in FinalUserCredit
        var clientIdsToReset = plansToReset
            .Select(p => p.ClientId)
            .Distinct()
            .ToList();

        if (clientIdsToReset.Any())
        {
            var usersCredits = await _context.FinalUserCredit
                .Where(f => clientIdsToReset.Contains(f.ClientId))
                .ToListAsync();

            foreach (var credit in usersCredits)
            {
                credit.LimitUsed = 0;
                credit.UpdatedAt = DateTime.UtcNow;
            }

            if (usersCredits.Any())
                Console.WriteLine($"✅ Reset monthly LimitUsed for {usersCredits.Count} users.");
        }

        // ✅ Step 6 — Save changes
        await _context.SaveChangesAsync();

        Console.WriteLine($"🎯 [{DateTime.Now}] MonthlyCreditResetJob completed successfully.\n");
    }
}

