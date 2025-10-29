using Microsoft.EntityFrameworkCore;
using PitchGenApi.Database;
using PitchGenApi.Model;
using PitchGenApi.Model.DTOs;
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
            if (stripeEvent.Data.Object is not Stripe.Checkout.Session session)
            {
                return;
            }

            var userId = session.ClientReferenceId;
            var planId = session.Metadata.ContainsKey("Plan") ? session.Metadata["Plan"] : "";
            var stripeCustomerId = session.CustomerId;
            var stripeSubscriptionId = session.SubscriptionId;

            if (string.IsNullOrEmpty(userId))
            {
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
            }
            else
            {
                existing.Status = "Active";
                existing.PlanId = planId ?? existing.PlanId;
                existing.StripeCustomerId = stripeCustomerId ?? existing.StripeCustomerId;
            }

            await _context.SaveChangesAsync();
        }
        public async Task HandleInvoicePaidAsync(Event stripeEvent)
        {
            // Extract invoice object
            if (stripeEvent.Data.Object is not Stripe.Invoice invoice)
                return;

            // Extract Subscription ID
            var subscriptionId = invoice.Lines?.Data?.FirstOrDefault()?.Parent?
                         .SubscriptionItemDetails?.Subscription;

            // Extract metadata from first line item
            var firstLine = invoice.Lines?.Data?.FirstOrDefault();
            string? userId = null;
            string? planId = null;

            if (firstLine?.Metadata != null)
            {
                userId = firstLine.Metadata.TryGetValue("app_user_id", out string uid) ? uid : null;
                planId = firstLine.Metadata.TryGetValue("plan", out string pid) ? pid : null;
            }

            if (string.IsNullOrEmpty(userId))
                return;

            if (!int.TryParse(userId, out int clientId))
                return;

            // Find existing subscription
            var existing = await _context.StripeSubscription
                .FirstOrDefaultAsync(x => x.UserId == userId && x.StripeSubscriptionId == subscriptionId);

            // Create or update subscription
            if (existing == null)
            {
                var newRecord = new StripeSubscription
                {
                    UserId = userId,
                    StripeCustomerId = invoice.CustomerId ?? "",
                    StripeSubscriptionId = subscriptionId ?? "",
                    PlanId = planId ?? "",
                    StartDate = DateTime.UtcNow,
                    EndDate = DateTime.UtcNow.AddMonths(1),
                    Status = "Active"
                };

                _context.StripeSubscription.Add(newRecord);
            }
            else
            {
                existing.Status = "Active";
            }

            await _context.SaveChangesAsync();

            // Grant credits or features after successful payment
            await SaveUserCreditsAsync(clientId, planId, subscriptionId);
        }

        public async Task HandleSubscriptionCancelledAsync(Event stripeEvent)
        {

            if (stripeEvent.Data.Object is not Stripe.Subscription subscription)
                return;

            var sub = await _context.StripeSubscription
                .FirstOrDefaultAsync(x => x.StripeSubscriptionId == subscription.Id);

            if (sub != null)
            {
                sub.Status = "Cancelled";
                sub.EndDate = DateTime.UtcNow;
                await _context.SaveChangesAsync();
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
        public async Task<StripeInvoiceResponse?> GetInvoiceDetailsAsync(string invoiceId)
        {
            if (string.IsNullOrWhiteSpace(invoiceId))
                throw new ArgumentException("Invoice ID cannot be null or empty.", nameof(invoiceId));

            var service = new InvoiceService();

            try
            {
                var invoice = await service.GetAsync(invoiceId);

                if (invoice == null)
                    throw new Exception($"Invoice not found for ID: {invoiceId}");

                // Map Stripe invoice to custom model
                var response = new StripeInvoiceResponse
                {
                    InvoiceId = invoice.Id,
                    CustomerEmail = invoice.CustomerEmail,
                    CustomerName = invoice.CustomerName,
                    InvoiceNumber = invoice.Number,
                    InvoiceDate = invoice.Created.ToUniversalTime(),
                    AmountPaid = (decimal)(invoice.AmountPaid / 100.0m), // convert cents to currency
                    InvoicePdfUrl = invoice.InvoicePdf
                };

                return response;
            }
            catch (StripeException ex)
            {
                throw new Exception($"Stripe API error: {ex.Message}", ex);
            }
            catch (Exception ex)
            {
                throw new Exception($"Error fetching invoice details: {ex.Message}", ex);
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

            }
            catch (Exception ex)
            {
                Console.WriteLine($"💥 Error saving user credits: {ex.Message}");
            }
        }

    }
}
