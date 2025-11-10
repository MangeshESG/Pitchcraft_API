using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using PitchGenApi.Database;
using PitchGenApi.Model;
using PitchGenApi.Model.DTOs;
using Stripe;
using System.Numerics;
using UglyToad.PdfPig.Graphics.Operations.PathPainting;

namespace PitchGenApi.Repositories
{
    public class StripeRepository : IStripeRepository
    {
        private readonly AppDbContext _context;

        public StripeRepository(AppDbContext context)
        {
            _context = context;
        }

        public async Task<string> CreateCreditPurchaseIntentAsync(string userId, int credits)
        {
            if (credits <= 0)
                throw new ArgumentException("Credits must be greater than zero.");

            // Check for existing Stripe customer
            var existingCustomerId = await _context.StripeSubscription
                .Where(c => c.UserId == userId)
                .Select(c => c.StripeCustomerId)
                .FirstOrDefaultAsync();

            var customerService = new CustomerService();

            // If no customer exists, create a new one (do not save in DB)
            if (string.IsNullOrEmpty(existingCustomerId))
            {
                var newCustomer = await customerService.CreateAsync(new CustomerCreateOptions
                {
                    Metadata = new Dictionary<string, string>
                    {
                        { "userId", userId }
                    }
                });

                existingCustomerId = newCustomer.Id;
            }

            decimal pricePerCredit = 0.20m; // USD
            decimal totalAmount = credits * pricePerCredit;
            long amountInCents = (long)(totalAmount * 100);
            var nextSubNumber = await _context.UserCredits.CountAsync() + 1;
            var formattedSubNumber = $"SUB-{nextSubNumber:D4}";
            var options = new PaymentIntentCreateOptions
            {
                Amount = amountInCents,
                Currency = "usd",
                Customer = existingCustomerId, // Attach existing or newly created customer
                Metadata = new Dictionary<string, string>
                {
                    { "app_user_id", userId },
                    { "credits", credits.ToString() },
                    { "flag", "custom_credit" },
                    { "subscription_number", formattedSubNumber }
                },
                AutomaticPaymentMethods = new PaymentIntentAutomaticPaymentMethodsOptions
                {
                    Enabled = true
                }
            };

            var service = new PaymentIntentService();
            var paymentIntent = await service.CreateAsync(options);

            return paymentIntent.ClientSecret;
        }

        public async Task<CreateSubscriptionResponse> CreateSubscriptionAsync(CreateSubscriptionRequest req)
        {
            var user = await _context.StripeSubscription.FirstOrDefaultAsync(u => u.UserId == req.UserId);
            var client = await _context.ClientDetails
                .FirstOrDefaultAsync(u => u.Id == Convert.ToInt32(req.UserId));

            string customerId = user?.StripeCustomerId ?? string.Empty;

            if (string.IsNullOrEmpty(customerId))
            {
                var customerService = new CustomerService();
                var customer = await customerService.CreateAsync(new CustomerCreateOptions
                {
                    Email = client?.Email ?? "noemail@example.com",
                    Metadata = new Dictionary<string, string>
            {
                { "app_user_id", req.UserId }
            }
                });

                customerId = customer.Id;
            }

            var nextSubNumber = await _context.UserCredits.CountAsync() + 1;
            var formattedSubNumber = $"SUB-{nextSubNumber:D4}";

            // ✅ Use the interval from request (Monthly or Yearly)
            var intervalValue = string.Equals(req.Interval, "Yearly", StringComparison.OrdinalIgnoreCase)
                ? "Yearly"
                : "Monthly";

            var subOptions = new SubscriptionCreateOptions
            {
                Customer = customerId,
                Items = new List<SubscriptionItemOptions>
                {
                    new SubscriptionItemOptions { Price = req.PriceId }
                },
                PaymentBehavior = "default_incomplete",
                Expand = new List<string>
                        {
                            "latest_invoice.payment_intent",
                            "latest_invoice.payments"
                        },
                Metadata = new Dictionary<string, string>
                {
                        { "app_user_id", req.UserId },
                        { "plan", req.PriceId },
                        { "subscription_number", formattedSubNumber },
                        { "interval", intervalValue } // ✅ dynamically set interval
                }
            };

            var subService = new SubscriptionService();
            var subscription = await subService.CreateAsync(subOptions);

            string? clientSecret = null;

            if (subscription.LatestInvoice != null && subscription.LatestInvoice is Invoice invoice)
            {
                if (invoice.Payments != null && invoice.Payments.Data.Count > 0)
                {
                    var firstPayment = invoice.Payments.Data[0];
                    string? paymentIntentId = firstPayment.Payment?.PaymentIntentId;

                    if (!string.IsNullOrEmpty(paymentIntentId))
                    {
                        var paymentIntentService = new PaymentIntentService();

                        var updateOptions = new PaymentIntentUpdateOptions
                        {
                            ReceiptEmail = client?.Email ?? "noemail@example.com"
                        };

                        await paymentIntentService.UpdateAsync(paymentIntentId, updateOptions);

                        var paymentIntent = await paymentIntentService.GetAsync(paymentIntentId);
                        clientSecret = paymentIntent.ClientSecret;
                    }
                }
            }

            var dbSub = new StripeSubscription
            {
                UserId = req.UserId,
                StripeSubscriptionId = subscription.Id,
                StripeCustomerId = customerId,
                PlanId = req.PriceId,
                Status = subscription.Status,
                SubscriptionNumber = formattedSubNumber,
                StartDate = DateTime.UtcNow,
                Interval = intervalValue
            };

            _context.StripeSubscription.Add(dbSub);
            await _context.SaveChangesAsync();

            return new CreateSubscriptionResponse
            {
                SubscriptionNumber = formattedSubNumber,
                SubscriptionId = subscription.Id,
                ClientSecret = clientSecret
            };
        }


        //public async Task<StripeSubscriptionResponse> GetAllSubscriptionsByCustomerAsync(string clientId, int limit = 10, string? startingAfter = null)
        //{
        //    var subscriptionRecord = await _context.StripeSubscription
        //        .FirstOrDefaultAsync(s => s.UserId == clientId);

        //    var customerId = subscriptionRecord?.StripeCustomerId;

        //    var service = new SubscriptionService();

        //    var options = new SubscriptionListOptions
        //    {
        //        Customer = customerId,
        //        Limit = limit,
        //        Expand = new List<string>
        //        {
        //            "data.customer",
        //            "data.items.data.price"
        //        }
        //    };

        //    if (!string.IsNullOrEmpty(startingAfter))
        //        options.StartingAfter = startingAfter;

        //    var subscriptions = await service.ListAsync(options);

        //    var productService = new ProductService();
        //    var result = new List<object>();

        //    foreach (var s in subscriptions.Data)
        //    {
        //        var firstItem = s.Items?.Data?.FirstOrDefault();
        //        var price = firstItem?.Price;
        //        string planName = price?.Nickname;
        //        decimal? planAmount = price?.UnitAmountDecimal / 100;
        //        string interval = price?.Recurring?.Interval;

        //        if (string.IsNullOrEmpty(planName) && price?.ProductId != null)
        //        {
        //            var product = await productService.GetAsync(price.ProductId);
        //            planName = product?.Name;
        //        }
        //        string subscriptionNumber = s.Metadata != null && s.Metadata.ContainsKey("subscription_number")
        //            ? s.Metadata["subscription_number"]
        //            : "N/A";
        //        string Status = s.Status;
        //        var StartDate = s.StartDate;
        //        var EndDate = s.Items.Data.First().CurrentPeriodEnd;
        //        var CustomerEmail = (s.Customer as Stripe.Customer)?.Email ?? "N/A";

        //        result.Add(new
        //        {
        //            SubscriptionId = subscriptionNumber,
        //            Status,
        //            PlanName = planName ?? "N/A",
        //            PlanAmount = planAmount ?? 0,
        //            Interval = interval ?? "N/A",
        //            StartDate,
        //            EndDate,
        //            CustomerEmail
        //        });
        //    }

        //    return new StripeSubscriptionResponse
        //    {
        //        Data = result,
        //        HasMore = subscriptions.HasMore,
        //        NextCursor = subscriptions.Data.LastOrDefault()?.Id
        //    };
        //}
        public async Task<PlanHistoryPagedResult<object>> GetPlanHistoryByClientIdAsync(int clientId, int pageNumber = 1, int pageSize = 10)
        {
            if (pageNumber < 1)
                pageNumber = 1;

            if (pageSize < 1)
                pageSize = 10;

            var query = _context.UserCredits
                .Where(x => x.ClientId == clientId)
                .OrderByDescending(x => x.CreatedAt);

            var totalRecords = await query.CountAsync();
            var totalPages = (int)Math.Ceiling(totalRecords / (double)pageSize);

            var data = await query
                .Skip((pageNumber - 1) * pageSize)
                .Take(pageSize)
                .Select(x => new
                {
                    SubscriptionId = x.SubscriptionNumber ?? "N/A",
                    Status = x.Status ?? "N/A",
                    PlanName = x.Plane ?? "N/A",
                    PlanAmount = x.Amount == null ? 0 : x.Amount,
                    x.Interval,
                    x.StartDate,
                    x.EndDate,

                })
                .ToListAsync();

            return new PlanHistoryPagedResult<object>
            {
                Items = data,
                TotalRecords = totalRecords,
                TotalPages = totalPages,
                CurrentPage = pageNumber,
                PageSize = pageSize
            };
        }

        public async Task HandleWebhookEventAsync(Event stripeEvent)
        {
            switch (stripeEvent.Type)
            {
                case "invoice.paid":
                    await HandleInvoicePaidAsync(stripeEvent);
                    break;

                case "customer.subscription.deleted":
                    await HandleSubscriptionCancelledAsync(stripeEvent);
                    break;

                case "payment_intent.succeeded":
                    await HandlePaymentIntentSucceededAsync(stripeEvent);
                    break;

                default:
                    break;
            }
        }
        public async Task HandlePaymentIntentSucceededAsync(Event stripeEvent)
        {
            try
            {
                var paymentIntent = stripeEvent.Data.Object as Stripe.PaymentIntent;
                if (paymentIntent == null)
                    return;

                // ✅ Extract metadata safely
                var clientIdStr = paymentIntent.Metadata.TryGetValue("app_user_id", out var userIdValue) ? userIdValue : null;
                var creditsStr = paymentIntent.Metadata.TryGetValue("credits", out var creditsValue) ? creditsValue : null;
                var flag = paymentIntent.Metadata.TryGetValue("flag", out var flagValue) ? flagValue : null;
                var SubcribtionNumber = paymentIntent.Metadata.TryGetValue("subscription_number", out string sn) ? sn : null;

                if (string.IsNullOrEmpty(clientIdStr))
                {
                    Console.WriteLine("❌ Missing 'app_user_id' in payment metadata.");
                    return;
                }

                // ✅ Convert userId (string) → int (ClientId)
                if (!int.TryParse(clientIdStr, out int clientId))
                {
                    Console.WriteLine($"❌ Invalid ClientId format: {clientIdStr}");
                    return;
                }

                // ✅ Only handle custom credit purchases
                if (flag != "custom_credit")
                {
                    Console.WriteLine($"ℹ️ Skipping — flag is not 'custom_credit' for user {clientId}");
                    return;
                }

                // ✅ Parse and validate credits
                var amountUsd = paymentIntent.AmountReceived / 100m; // Stripe sends in cents
                if (!int.TryParse(creditsStr, out int creditsPurchased))
                    creditsPurchased = 0;

                if (creditsPurchased <= 0)
                {
                    Console.WriteLine($"⚠️ Invalid or missing credits: {creditsStr}");
                    return;
                }

                // ✅ Ensure client exists
                var client = await _context.ClientDetails.FirstOrDefaultAsync(x => x.Id == clientId);
                if (client == null)
                {
                    Console.WriteLine($"❌ No client found for ID: {clientId}");
                    return;
                }
                var StartDate = DateTime.UtcNow;
                var intcredit = Convert.ToInt32(creditsStr);

                await SaveUserCreditsAsync(clientId, "Custom Credit", null, SubcribtionNumber, StartDate, null, null, amountUsd, intcredit);

                Console.WriteLine($"🆕 Added new credit record for user {clientId} — {creditsPurchased} credits (${amountUsd}).");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error in HandlePaymentIntentSucceededAsync: {ex.Message}");
            }
        }

        public async Task<object?> GetActivePlanStatusAndPlaneAsync(int clientId)
        {
            var record = await _context.UserCredits
                .Where(u => u.ClientId == clientId && u.Status == "active")
                .OrderByDescending(u => u.CreatedAt)
                .Select(u => new
                {
                    u.Status,
                    u.Plane,
                    u.Interval
                })
                .FirstOrDefaultAsync();

            return record;
        }

        public async Task HandleInvoicePaidAsync(Event stripeEvent)
        {
            if (stripeEvent.Data.Object is not Stripe.Invoice invoice)
                return;

            var subscriptionId = invoice.Lines?.Data?.FirstOrDefault()?.Parent?
                         .SubscriptionItemDetails?.Subscription;
            var amountPaid = invoice.AmountPaid / 100m;
            var firstLine = invoice.Lines?.Data?.FirstOrDefault();
            string? userId = null;
            string? planId = null;
            string? SubcribtionNumber = null;
            string? interval = null;
            var StartDate = DateTime.UtcNow;
            var EndDate = StartDate.AddMonths(1);
            if (firstLine?.Metadata != null)
            {
                userId = firstLine.Metadata.TryGetValue("app_user_id", out string uid) ? uid : null;
                planId = firstLine.Metadata.TryGetValue("plan", out string pid) ? pid : null;
                SubcribtionNumber = firstLine.Metadata.TryGetValue("subscription_number", out string sn) ? sn : null;
                interval = firstLine.Metadata.TryGetValue("interval", out string intr) ? intr : null;

            }

            if (interval == "Yearly")
            {
                EndDate = StartDate.AddYears(1);
            }

            if (string.IsNullOrEmpty(userId))
                return;

            if (!int.TryParse(userId, out int clientId))
                return;

            var existing = await _context.StripeSubscription
                .FirstOrDefaultAsync(x => x.UserId == userId && x.StripeSubscriptionId == subscriptionId);

            if (existing == null)
            {
                var newRecord = new StripeSubscription
                {
                    UserId = userId,
                    StripeCustomerId = invoice.CustomerId ?? "",
                    StripeSubscriptionId = subscriptionId ?? "",
                    PlanId = planId ?? "",
                    StartDate = StartDate,
                    EndDate = EndDate,
                    Status = "Active"
                };

                _context.StripeSubscription.Add(newRecord);
            }
            else
            {
                existing.Status = "Active";
            }

            await _context.SaveChangesAsync();

            await SaveUserCreditsAsync(clientId, planId, subscriptionId, SubcribtionNumber, StartDate, EndDate, interval, amountPaid, null);
        }

        public async Task HandleSubscriptionCancelledAsync(Event stripeEvent)
        {

            if (stripeEvent.Data.Object is not Stripe.Subscription subscription)
                return;

            var sub = await _context.StripeSubscription
                .FirstOrDefaultAsync(x => x.StripeSubscriptionId == subscription.Id);

            if (sub != null)
            {
                sub.Status = subscription.Status;
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
                CreditsExpiry.Status = subscription.Status;
                await _context.SaveChangesAsync();
            }
            else
            {
                Console.WriteLine($"⚠️ Subscription {subscription.Id} not found in database.");
            }
        }

        public async Task SaveUserCreditsAsync(int userId, string planId, string stripeSubscriptionId, string SubcribtionNumber, DateTime StartDate, DateTime? EndDate, string interval, decimal amount, int? CreditsCount)
        {
            try
            {
                int? credits = 0;
                string planName = "Unknown";
                int monthlyLimit = 0;

                // 🔹 Plan logic
                switch (planId)
                {
                    case "Basic":
                        credits = 100;
                        planName = "Basic";
                        amount = 0;
                        monthlyLimit = 100;
                        break;

                    case "price_1SMmZiHDCkj9hBmZ5u4UA72M":
                    case "price_standard":
                        credits = 1000;
                        planName = "Standard";
                        monthlyLimit = 1000;
                        break;

                    case "price_1SMmZ6HDCkj9hBmZNyIzVJQL":
                    case "price_premium":
                        credits = 2000;
                        planName = "Premium";
                        monthlyLimit = 2000;
                        break;

                    case "price_1SPgOFHDCkj9hBmZxSnUTzAT":
                        credits = 12000;
                        planName = "Standard";
                        monthlyLimit = 1000;
                        break;

                    case "price_1SPh0hHDCkj9hBmZXtVBJ1QG":
                        credits = 24000;
                        planName = "Premium";
                        monthlyLimit = 2000;
                        break;

                    case "Custom Credit":
                        credits = CreditsCount;
                        planName = "Custom Credit";
                        break;

                    default:
                        Console.WriteLine($"⚠️ Unknown plan ID: {planId}");
                        return;
                }

                var now = DateTime.UtcNow;

                // ✅ Step 1 — Create UserCredits record
                var userCredits = new UserCredits
                {
                    ClientId = userId,
                    Credits = credits,
                    TotalPurchesdCredit = credits,
                    CreatedAt = now,
                    Plane = planName,
                    StripeSubscriptionId = stripeSubscriptionId,
                    SubscriptionNumber = SubcribtionNumber,
                    Status = "Active",
                    StartDate = StartDate,
                    EndDate = EndDate,
                    Interval = interval,
                    Amount = amount
                };

                // ✅ Only set ResetDate for non-custom plans
                if (planId != "Custom Credit")
                {
                    userCredits.ResetDate = now.AddMonths(1);
                }

                _context.UserCredits.Add(userCredits);
                await _context.SaveChangesAsync();

                // ✅ Step 2 — Fetch or create FinalUserCredit
                var finalCredit = await _context.FinalUserCredit
                    .FirstOrDefaultAsync(f => f.ClientId == userId);

                if (planId == "Custom Credit")
                {
                    // 🔹 Custom Credit Logic: only increase total credit
                    if (finalCredit != null)
                    {
                        finalCredit.UpdatedAt = DateTime.UtcNow;
                        finalCredit.CustomLimit = (finalCredit.CustomLimit ?? 0) + CreditsCount;
                        _context.FinalUserCredit.Update(finalCredit);
                    }
                    else
                    {
                        // If no record exists, create new one
                        finalCredit = new FinalUserCredit
                        {
                            ClientId = userId,
                            TotalCredit = 0,
                            CreatedAt = DateTime.UtcNow,
                            MonthlyLimit = 0,
                            CustomLimit = CreditsCount,
                            UsedCredit = 0,
                            LimitUsed = 0
                        };
                        _context.FinalUserCredit.Add(finalCredit);
                    }
                }
                else
                {
                    // 🔹 Regular Plan Logic
                    var totalActiveCredits = (await _context.FinalUserCredit
                        .Where(u => u.ClientId == userId )
                        .SumAsync(u => (int?)u.TotalCredit)) ?? 0;

                    if (finalCredit != null)
                    {
                        finalCredit.TotalCredit += credits;
                        finalCredit.MonthlyLimit = monthlyLimit > 0 ? monthlyLimit : finalCredit.MonthlyLimit;
                        finalCredit.UpdatedAt = DateTime.UtcNow;
                        _context.FinalUserCredit.Update(finalCredit);
                    }
                    else
                    {
                        finalCredit = new FinalUserCredit
                        {
                            ClientId = userId,
                            TotalCredit = credits,
                            CreatedAt = DateTime.UtcNow,
                            MonthlyLimit = monthlyLimit,
                            UsedCredit = 0,
                            LimitUsed = 0
                        };
                        _context.FinalUserCredit.Add(finalCredit);
                    }
                }

                await _context.SaveChangesAsync();

                Console.WriteLine($"✅ FinalUserCredit updated successfully for ClientId: {userId}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error in SaveUserCreditsAsync: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
                throw;
            }
        }
    }
}