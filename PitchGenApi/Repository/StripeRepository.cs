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

            var options = new PaymentIntentCreateOptions
            {
                Amount = amountInCents,
                Currency = "usd",
                Customer = existingCustomerId, // Attach existing or newly created customer
                Metadata = new Dictionary<string, string>
                {
                    { "userId", userId },
                    { "credits", credits.ToString() }
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
                    Email = req.Email ?? client?.Email ?? "noemail@example.com",
                    Metadata = new Dictionary<string, string> { { "app_user_id", req.UserId } }
                });

                customerId = customer.Id;
            }

            var nextSubNumber = await _context.UserCredits.CountAsync() + 1;
            var formattedSubNumber = $"SUB-{nextSubNumber:D4}";

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
                    { "subscription_number", formattedSubNumber }
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
                            ReceiptEmail = req.Email ?? client?.Email ?? "noemail@example.com"
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
                StartDate = DateTime.UtcNow
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
                    Interval = "Monthly", // or map from your plan if available
                    StartDate = x.StartDate != default ? x.StartDate : DateTime.MinValue,
                    EndDate = x.EndDate != default ? x.EndDate : DateTime.MinValue,
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

                case "invoice.payment_succeeded":
                    await HandleSubscriptionCancelledAsync(stripeEvent);
                    break;

                default:
                    break;
            }
        }

        public async Task HandleInvoicePaidAsync(Event stripeEvent)
        {
            if (stripeEvent.Data.Object is not Stripe.Invoice invoice)
                return;

            var subscriptionId = invoice.Lines?.Data?.FirstOrDefault()?.Parent?
                         .SubscriptionItemDetails?.Subscription;

            var firstLine = invoice.Lines?.Data?.FirstOrDefault();
            string? userId = null;
            string? planId = null;
            string? SubcribtionNumber = null;
            var StartDate = invoice.PeriodStart;
            var EndDate = invoice.PeriodEnd;
            if (firstLine?.Metadata != null)
            {
                userId = firstLine.Metadata.TryGetValue("app_user_id", out string uid) ? uid : null;
                planId = firstLine.Metadata.TryGetValue("plan", out string pid) ? pid : null;
                SubcribtionNumber = firstLine.Metadata.TryGetValue("subscription_number", out string sn) ? sn : null;
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

            await SaveUserCreditsAsync(clientId, planId, subscriptionId, SubcribtionNumber, StartDate, EndDate);
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

        public async Task<StripeInvoiceResponse?> GetInvoiceDetailsAsync(string invoiceId)
        {
            if (string.IsNullOrWhiteSpace(invoiceId))
                throw new ArgumentException("Invoice ID cannot be null or empty.", nameof(invoiceId));

            var service = new InvoiceService();

            var invoice = await service.GetAsync(invoiceId);

            if (invoice == null)
                throw new Exception($"Invoice not found for ID: {invoiceId}");

            var response = new StripeInvoiceResponse
            {
                InvoiceId = invoice.Id,
                CustomerEmail = invoice.CustomerEmail,
                CustomerName = invoice.CustomerName,
                InvoiceNumber = invoice.Number,
                InvoiceDate = invoice.Created.ToUniversalTime(),
                AmountPaid = (decimal)(invoice.AmountPaid / 100.0m),
                InvoicePdfUrl = invoice.InvoicePdf
            };

            return response;
        }

        public async Task SaveUserCreditsAsync(int userId, string planId, string stripeSubscriptionId, string SubcribtionNumber, DateTime StartDate, DateTime EndDate)
        {
            int credits = 0;
            string planName = "Unknown";

            switch (planId)
            {
                case "Basic":
                case "price_basic":
                    credits = 100;
                    planName = "Basic";
                    break;

                case "price_1SMmZiHDCkj9hBmZ5u4UA72M":
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
                    return;
            }

            var now = DateTime.UtcNow;

            var userCredits = new UserCredits
            {
                ClientId = userId,
                Credits = credits,
                CreatedAt = now,
                Plane = planName,
                StripeSubscriptionId = stripeSubscriptionId,
                SubscriptionNumber = SubcribtionNumber,
                Status = "Active",
                StartDate = now,
                EndDate = now.AddMonths(1)
            };

            _context.UserCredits.Add(userCredits);
            await _context.SaveChangesAsync();
        }
    }
}