using Microsoft.AspNetCore.Mvc;
using Stripe.Checkout;
using Stripe;
using PitchGenApi.Model;
using PitchGenApi.Database;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Threading;
using PitchGenApi.Repositories;
using Microsoft.IdentityModel.Tokens;
using PitchGenApi.Model.DTOs;

namespace PitchGenApi.Controllers
{
    [ApiController]
    [Route("api/stripe")]
    public class StripeController : ControllerBase
    {
        private readonly IConfiguration _config;
        private readonly IStripeRepository _stripeRepository;
        private readonly AppDbContext _context;
        private readonly string _webhookSecret;

        public StripeController(IConfiguration config, IStripeRepository stripeRepository, AppDbContext context)
        {
            _config = config;
            _context = context;
            _stripeRepository = stripeRepository;
            _webhookSecret = _config["Stripe:WebhookSecret"]; // ✅ from appsettings
            StripeConfiguration.ApiKey = _config["Stripe:SecretKey"];
        }

        [HttpPost("create-subscription")]
        public async Task<IActionResult> CreateSubscription([FromBody] CreateSubscriptionRequest req)
        {
            try
            {
                // 1️⃣ Ensure Stripe customer exists — create if missing
                var user = await _context.StripeSubscription.FirstOrDefaultAsync(u => u.UserId == req.UserId);
                var client = await _context.ClientDetails
                    .FirstOrDefaultAsync(u => u.Id == Convert.ToInt32(req.UserId));

                string customerId = user?.StripeCustomerId ?? string.Empty;

                // 🔹 Step 2: If no Stripe customer — create a new one (don’t save in DB)
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

                // 2️⃣ Generate a unique subscription number before creating Stripe subscription
                var nextSubNumber = await _context.UserCredits.CountAsync() + 1;
                var formattedSubNumber = $"SUB-{nextSubNumber:D4}"; // e.g. SUB-0001

                // 3️⃣ Create subscription with metadata (including your custom subscription number)
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
                { "subscription_number", formattedSubNumber } // ✅ custom subscription number
            }
                };

                var subService = new SubscriptionService();
                var subscription = await subService.CreateAsync(subOptions);

                // 4️⃣ Fetch PaymentIntent client secret (if available)
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

                            // 🔹 Auto-send Stripe receipt email
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

                // 5️⃣ Save subscription info (including number) in DB
                var dbSub = new StripeSubscription
                {
                    UserId = req.UserId,
                    StripeSubscriptionId = subscription.Id,
                    StripeCustomerId = customerId,
                    PlanId = req.PriceId,
                    Status = subscription.Status,
                    SubscriptionNumber = formattedSubNumber, // ✅ store custom number
                    StartDate = DateTime.UtcNow
                };

                _context.StripeSubscription.Add(dbSub);
                await _context.SaveChangesAsync();

                // 6️⃣ Return everything to frontend
                return Ok(new
                {
                    subscriptionNumber = formattedSubNumber,
                    subscriptionId = subscription.Id,
                    clientSecret
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }

        [HttpGet("{invoiceId}")]
        public IActionResult GetInvoiceUrls(string invoiceId)
        {
            try
            {
                var invoice = _stripeRepository.GetInvoiceDetailsAsync(invoiceId);

                if (invoice == null)
                    return NotFound(new { message = "Invoice not found" });

                return Ok(new
                {
                    InvoiceDwtils = invoice
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpGet("customer/{ClientId}/subscriptions")]
        public async Task<IActionResult> GetAllSubscriptionsByCustomer(
        string ClientId,
        int limit = 10,
        string? startingAfter = null)
        {
            // ✅ Get Stripe Customer ID from your local DB
            var subscriptionRecord = await _context.StripeSubscription
                .FirstOrDefaultAsync(s => s.UserId == ClientId);

            //if (subscriptionRecord == null || string.IsNullOrWhiteSpace(subscriptionRecord.StripeCustomerId))
            //    return BadRequest(new { message = "No Stripe customer found for this client." });

            var customerId = subscriptionRecord.StripeCustomerId;

            try
            {
                var service = new SubscriptionService();

                // ⚙️ Expand only up to price level to avoid deep expand limit
                var options = new SubscriptionListOptions
                {
                    Customer = customerId,
                    Limit = limit,
                    Expand = new List<string>
                    {
                        "data.customer",
                        "data.items.data.price"
                    }
                };

                if (!string.IsNullOrEmpty(startingAfter))
                    options.StartingAfter = startingAfter;

                var subscriptions = await service.ListAsync(options);

                //if (subscriptions == null || subscriptions.Data.Count == 0)
                //    return Ok(new { message = "No subscriptions found for this customer." });

                var productService = new ProductService();
                var result = new List<object>();

                foreach (var s in subscriptions.Data)
                {
                    var firstItem = s.Items?.Data?.FirstOrDefault();
                    var price = firstItem?.Price;
                    string planName = price?.Nickname;
                    decimal? planAmount = price?.UnitAmountDecimal / 100;
                    string interval = price?.Recurring?.Interval;

                    // ⚙️ Fetch product name manually (safe way, avoids deep expand)
                    if (string.IsNullOrEmpty(planName) && price?.ProductId != null)
                    {
                        var product = await productService.GetAsync(price.ProductId);
                        planName = product?.Name;
                    }
                    string subscriptionNumber = s.Metadata != null && s.Metadata.ContainsKey("subscription_number")
                        ? s.Metadata["subscription_number"]
                        : "N/A";
                    string Status = s.Status;
                    var StartDate = s.StartDate;
                    var EndDate = s.Items.Data.First().CurrentPeriodEnd;
                    var CustomerEmail = (s.Customer as Stripe.Customer)?.Email ?? "N/A";

                    result.Add(new
                    {
                        SubscriptionId = subscriptionNumber,
                        Status,
                        PlanName = planName ?? "N/A",
                        PlanAmount = planAmount ?? 0,
                        Interval = interval ?? "N/A",
                        StartDate,
                        EndDate,
                        //CancelAtPeriodEnd = s.CancelAtPeriodEnd,
                        CustomerEmail
                    });
                }

                // 🔁 Include pagination info in response
                return Ok(new
                {
                    Data = result,
                    HasMore = subscriptions.HasMore,
                    NextCursor = subscriptions.Data.LastOrDefault()?.Id
                });
            }
            catch (StripeException ex)
            {
                return StatusCode(500, new { message = "Stripe error", error = ex.Message });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Server error", error = ex.Message });
            }
        }

        [HttpPost("webhook")]
        public async Task<IActionResult> Webhook()
        {
            var json = await new StreamReader(HttpContext.Request.Body).ReadToEndAsync();

            Event stripeEvent;
            try
            {
                stripeEvent = EventUtility.ConstructEvent(
                    json,
                    Request.Headers["Stripe-Signature"],
                    _webhookSecret // ✅ dynamic from config
                );
            }
            catch (StripeException ex)
            {
                return BadRequest();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Webhook error: {ex.Message}");
                return BadRequest();
            }

            // ✅ Route events to repo
            switch (stripeEvent.Type)
            {
                //case "checkout.session.completed":
                //    await _stripeRepository.HandleCheckoutCompletedAsync(stripeEvent);
                //    break;

                case "invoice.paid":
                    await _stripeRepository.HandleInvoicePaidAsync(stripeEvent);
                    break;

                case "customer.subscription.deleted":
                    await _stripeRepository.HandleSubscriptionCancelledAsync(stripeEvent);
                    break;

                case "invoice.payment_succeeded":
                    await _stripeRepository.HandleSubscriptionCancelledAsync(stripeEvent);
                    break;

                default:
                    break;
            }

            return Ok();
        }
    }
}