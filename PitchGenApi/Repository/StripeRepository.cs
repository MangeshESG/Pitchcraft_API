using Microsoft.EntityFrameworkCore;
using PitchGenApi.Database;
using PitchGenApi.Model;
using Stripe;

namespace PitchGenApi.Repositories
{
    public class StripeRepository : IStripeRepository
    {
        private readonly AppDbContext _context;

        public StripeRepository(AppDbContext context)
        {
            _context = context;
        }

        public async Task HandleCheckoutCompletedAsync(Event stripeEvent)
        {
            Console.WriteLine("🎉 Handling event: checkout.session.completed");

            if (stripeEvent.Data.Object is not Stripe.Checkout.Session session)
            {
                Console.WriteLine("⚠️ Session object not found.");
                return;
            }

            var userId = session.ClientReferenceId;
            var planId = session.Metadata.ContainsKey("Plan") ? session.Metadata["Plan"] : "";
            var stripeCustomerId = session.CustomerId;
            var stripeSubscriptionId = session.SubscriptionId;

            Console.WriteLine($"🧩 UserId: {userId}");
            Console.WriteLine($"📘 PlanId: {planId}");
            Console.WriteLine($"👤 CustomerId: {stripeCustomerId}");
            Console.WriteLine($"🧾 SubscriptionId: {stripeSubscriptionId}");

            if (string.IsNullOrEmpty(userId))
            {
                Console.WriteLine("⚠️ Missing UserId — skipping record.");
                return;
            }

            // ✅ Idempotent insert/update
            var existing = await _context.StripeSubscription
                .FirstOrDefaultAsync(x => x.StripeSubscriptionId == stripeSubscriptionId);

            if (int.TryParse(userId, out int clientId))
            {
                await SaveUserCreditsAsync(clientId, planId, stripeSubscriptionId);
            }
            else
            {
                Console.WriteLine($"⚠️ Invalid userId: {userId}, cannot convert to int");
            }

            if (existing == null)
            {
                var record = new StripeSubscription
                {
                    UserId = userId,
                    StripeCustomerId = stripeCustomerId ?? "",
                    StripeSubscriptionId = stripeSubscriptionId ?? "",
                    PlanId = planId ?? "",
                    StartDate = DateTime.UtcNow,
                    Status = "Active"
                };

                _context.StripeSubscription.Add(record);
                Console.WriteLine("✅ New subscription record created.");
            }
            else
            {
                existing.Status = "Active";
                existing.PlanId = planId ?? existing.PlanId;
                existing.StripeCustomerId = stripeCustomerId ?? existing.StripeCustomerId;
                Console.WriteLine("♻️ Subscription already exists — updated.");
            }

            await _context.SaveChangesAsync();
        }
        public async Task HandleInvoicePaidAsync(Event stripeEvent)
        {
            Console.WriteLine("💰 Handling event: invoice.paid");

            // 1️⃣ Extract invoice object from event
            if (stripeEvent.Data.Object is not Stripe.Invoice invoice)
            {
                Console.WriteLine("⚠️ Invoice object not found in event.");
                return;
            }

            Console.WriteLine($"🧾 Invoice ID: {invoice.Id}");
            Console.WriteLine($"💵 Status: {invoice.Status}");
            Console.WriteLine($"👤 Customer: {invoice.CustomerId}");

            // 2️⃣ Extract subscription ID (available directly on invoice)
            var subscriptionId = invoice.Lines?.Data?.FirstOrDefault()?.Parent?
             .SubscriptionItemDetails?.Subscription;
             Console.WriteLine($"🔗 Subscription ID: {subscriptionId}");

            Console.WriteLine($"🧩 SubscriptionId: {subscriptionId}");

            // 3️⃣ Extract first line item to get metadata (plan & user id)
            var firstLine = invoice.Lines?.Data?.FirstOrDefault();
            string? userId = null;
            string? planId = null;

            if (firstLine?.Metadata != null)
            {
                userId = firstLine.Metadata.TryGetValue("app_user_id", out string uid) ? uid : null;
                planId = firstLine.Metadata.TryGetValue("plan", out string pid) ? pid : null;
            }

            Console.WriteLine($"👤 UserId (metadata): {userId}");
            Console.WriteLine($"📘 PlanId (metadata): {planId}");

            if (string.IsNullOrEmpty(userId))
            {
                Console.WriteLine("⚠️ Missing UserId — skipping DB save.");
                return;
            }

            // 4️⃣ Check if subscription already exists
            var existing = await _context.StripeSubscription
                .FirstOrDefaultAsync(x => x.StripeSubscriptionId == subscriptionId);

            if (!int.TryParse(userId, out int clientId))
            {
                Console.WriteLine($"⚠️ Invalid userId '{userId}' cannot convert to int.");
                return;
            }

            // ✅ Add or update subscription record
            if (existing == null)
            {
                var newRecord = new StripeSubscription
                {
                    UserId = userId,
                    StripeCustomerId = invoice.CustomerId ?? "",
                    StripeSubscriptionId = subscriptionId ?? "",
                    PlanId = planId ?? "",
                    StartDate = DateTime.UtcNow,
                    Status = "Active"
                };

                _context.StripeSubscription.Add(newRecord);
                Console.WriteLine("✅ New subscription record created in DB.");
            }
            else
            {
                existing.Status = "Active";
                existing.PlanId = planId ?? existing.PlanId;
                existing.StripeCustomerId = invoice.CustomerId ?? existing.StripeCustomerId;
                Console.WriteLine("♻️ Subscription already exists — updated.");
            }

            await _context.SaveChangesAsync();

            // 5️⃣ Optional: Grant credits or features after payment
            await SaveUserCreditsAsync(clientId, planId, subscriptionId);
            Console.WriteLine("🎯 Payment success handled successfully.");
        }

        public async Task HandleSubscriptionCancelledAsync(Event stripeEvent)
        {
            Console.WriteLine("❌ Subscription cancelled event received.");

            if (stripeEvent.Data.Object is not Stripe.Subscription subscription)
                return;

            var sub = await _context.StripeSubscription
                .FirstOrDefaultAsync(x => x.StripeSubscriptionId == subscription.Id);

            if (sub != null)
            {
                sub.Status = "Cancelled";
                sub.EndDate = DateTime.UtcNow;
                await _context.SaveChangesAsync();
                Console.WriteLine($"✅ Subscription {subscription.Id} marked as Cancelled.");
            }
            else
            {
                Console.WriteLine($"⚠️ Subscription {subscription.Id} not found in database.");
            }
            
            var CreditsExpiry = await _context.UserCredits
                .FirstOrDefaultAsync(x => x.StripeSubscriptionId == subscription.Id);

            if (CreditsExpiry != null)
            {
                CreditsExpiry.Status = "Cancelled";
                await _context.SaveChangesAsync();
            }
            else
            {
                Console.WriteLine($"⚠️ Subscription {subscription.Id} not found in database.");
            }
        }

        public async Task SaveUserCreditsAsync(int userId, string planId, string stripeSubscriptionId)
        {
            try
            {
                // 🧮 Determine credits based on plan
                int credits = 0;
                string planName = "Unknown";

                switch (planId)
                {
                    case "Basic":
                    case "price_basic":
                        credits = 100;
                        planName = "Basic";
                        break;

                    case "price_1SMmZiHDCkj9hBmZ5u4UA72M": // 👈 your actual standard ID
                    case "standard":
                    case "price_standard":
                        credits = 500;
                        planName = "Standard";
                        break;

                    case "price_1SMmZ6HDCkj9hBmZNyIzVJQL":
                    case "price_premium":
                        credits = 1000;
                        planName = "Premium";
                        break;

                    default:
                        Console.WriteLine($"⚠️ Unknown plan ID: {planId}");
                        return; // Don’t insert unknown plans
                }

                var now = DateTime.UtcNow;

                var userCredits = new UserCredits
                {
                    ClientId = userId,
                    Credits = credits,
                    CreatedAt = now,
                    Plane = planName,
                    StripeSubscriptionId = stripeSubscriptionId,
                    Status = "Active",
                    StartDate = now,
                    EndDate = now.AddMonths(1)
                };

                _context.UserCredits.Add(userCredits);
                await _context.SaveChangesAsync();

                Console.WriteLine($"✅ Added {credits} credits for {planName} plan (UserId: {userId})");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"💥 Error saving user credits: {ex.Message}");
            }
        }

    }
}
