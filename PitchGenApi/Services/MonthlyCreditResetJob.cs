using Microsoft.EntityFrameworkCore;
using PitchGenApi.Database;
using System;
using System.Linq;
using System.Threading.Tasks;

public class MonthlyCreditResetJob
{
    private readonly AppDbContext _context;

    public MonthlyCreditResetJob(AppDbContext context)
    {
        _context = context;
    }

    public async Task Execute()
    {
        Console.WriteLine($"🕐 [{DateTime.Now}] MonthlyCreditResetJob started...");

        // Step 1 — Mark expired plans
        var expiredPlans = await _context.UserCredits
            .Where(u => u.Status == "active" && u.EndDate <= DateTime.UtcNow)
            .ToListAsync();

        foreach (var plan in expiredPlans)
            plan.Status = "expired";

        var customCredits = await _context.UserCredits
             .Where(c => c.Plane == "Custom Credit" && c.Credits == 0)
             .ToListAsync();

        foreach (var plan in customCredits)
            plan.Status = "Used";
        1
        if (expiredPlans.Any())
            Console.WriteLine($"⚠️  Marked {expiredPlans.Count} plans as expired.");

        // Step 2 — Find users to reset monthly usage
        var plansToReset = await _context.UserCredits
            .Where(u => u.Status == "active" && u.ResetDate <= DateTime.UtcNow)
            .ToListAsync();

        foreach (var plan in plansToReset)
            plan.ResetDate = (plan.ResetDate ?? DateTime.UtcNow).AddMonths(1);

        if (plansToReset.Any())
            Console.WriteLine($"🔄 Found {plansToReset.Count} active plans to reset monthly limit.");

        // Step 3 — Reset FinalUserCredit usage
        var clientIdsToReset = plansToReset
            .Select(p => p.ClientId)
            .Distinct()
            .ToList();

        if (clientIdsToReset.Count > 0)
        {
            var usersCredits = await _context.FinalUserCredit
                .Where(f => clientIdsToReset.Contains(f.ClientId))
                .ToListAsync();

            foreach (var credit in usersCredits)
            {
                credit.LimitUsed = 0;
            }

            if (usersCredits.Any())
                Console.WriteLine($"✅ Reset UsedCredit for {usersCredits.Count} users.");
        }

        // Step 4 — Save all changes
        await _context.SaveChangesAsync();

        Console.WriteLine($"🎯 [{DateTime.Now}] MonthlyCreditResetJob completed successfully.\n");
    }
}
