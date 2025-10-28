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
        [HttpPost("create-customer")]
        public async Task<IActionResult> CreateCustomer([FromBody] CreateCustomerRequest req)
        {
            // req.UserId => your internal user id (string)
            // req.Email optional

            // 1. check DB if user already has StripeCustomerId
            //var user = await _context.StripeSubscription.FirstOrDefaultAsync(u => u.UserId == req.UserId);
            ////if (user == null) return BadRequest("User not found");

            //if (!string.IsNullOrEmpty(user.StripeCustomerId))
            //    return Ok(new { stripeCustomerId = user.StripeCustomerId });

            var options = new CustomerCreateOptions
            {
                Email = req.Email,
                Metadata = new Dictionary<string, string> { { "app_user_id", req.UserId } }
            };
            var service = new CustomerService();
            var stripeCustomer = await service.CreateAsync(options);

            //user.StripeCustomerId = stripeCustomer.Id;
            //await _context.SaveChangesAsync();

            return Ok(new { stripeCustomerId = stripeCustomer.Id });
        }

        public class CreateCustomerRequest { public string UserId { get; set; } = ""; public string? Email { get; set; } }

        [HttpPost("create-subscription")]
        public async Task<IActionResult> CreateSubscription([FromBody] CreateSubscriptionRequest req)
        {
            try
            {
                // 1. Ensure Stripe customer exists — create if missing
                var user = await _context.StripeSubscription.FirstOrDefaultAsync(u => u.UserId == req.UserId);
                var client = await _context.ClientDetails
                    .FirstOrDefaultAsync(u => u.Id == Convert.ToInt32(req.UserId));
                string customerId = user?.StripeCustomerId ?? string.Empty;

                 //🔹 Step 2: If no Stripe customer — create a new one(but don't save in DB)
                if (string.IsNullOrEmpty(customerId))
                {
                    var customerService = new CustomerService();
                    var customer = await customerService.CreateAsync(new CustomerCreateOptions
                    {
                        Email = req.Email ?? client?.Email ?? "noemail@example.com",
                        Metadata = new Dictionary<string, string> { { "app_user_id", req.UserId } }
                    });

                    customerId = customer.Id;
                    Console.WriteLine($"✅ New Stripe customer created: {customerId}");
                }

                // 2. Create subscription with default_incomplete so we get payment intent client secret
                var subOptions = new SubscriptionCreateOptions
                {
                    Customer = customerId,
                    Items = new List<SubscriptionItemOptions> {
                        new SubscriptionItemOptions { Price = req.PriceId }
                    },
                                    PaymentBehavior = "default_incomplete",
                                    Expand = new List<string> {
                        "latest_invoice.payment_intent",
                        "latest_invoice.payments"
                    },
                                    Metadata = new Dictionary<string, string> {
                        { "app_user_id", req.UserId },
                        { "plan", req.PriceId }
                    }
                };


                var subService = new SubscriptionService();
                var subscription = await subService.CreateAsync(subOptions);

                // ✅ Fetch PaymentIntent ClientSecret (universal method)
                string? clientSecret = null;

                if (subscription.LatestInvoice != null && subscription.LatestInvoice is Invoice invoice)
                {
                    if (invoice.Payments != null && invoice.Payments.Data.Count > 0)
                    {
                        var firstPayment = invoice.Payments.Data[0];
                        object? paymentIntentObj = firstPayment.Payment?.PaymentIntentId;

                        string? paymentIntentId = null;

                        if (paymentIntentObj is string idString)
                        {
                            paymentIntentId = idString;
                        }
                        else if (paymentIntentObj is PaymentIntent piObject)
                        {
                            paymentIntentId = piObject.Id;
                        }

                        if (!string.IsNullOrEmpty(paymentIntentId))
                        {
                            var paymentIntentService = new PaymentIntentService();
                            var paymentIntent = await paymentIntentService.GetAsync(paymentIntentId);
                            clientSecret = paymentIntent.ClientSecret;
                        }
                    }
                }

                 //✅ Save subscription info in DB
                var dbSub = new StripeSubscription
                {
                    UserId = req.UserId,
                    StripeSubscriptionId = subscription.Id,
                    StripeCustomerId = customerId,
                    PlanId = req.PriceId,
                    Status = subscription.Status,
                    StartDate = DateTime.UtcNow
                };

                _context.StripeSubscription.Add(dbSub);
                await _context.SaveChangesAsync();

                return Ok(new
                {
                    subscriptionId = subscription.Id,
                    clientSecret
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine("CreateSubscription error: " + ex.Message);
                return BadRequest(new { error = ex.Message });
            }
        }

        public class CreateSubscriptionRequest
        {
            public string UserId { get; set; } = "";
            public string PriceId { get; set; } = ""; // price_xxx from Stripe
            public string? Email { get; set; }
        }

        // 🔹 Create checkout session
        //[HttpPost("create-checkout-session")]
        //public async Task<IActionResult> CreateCheckoutSession([FromBody] CheckoutRequest request)
        //{
        //    var options = new SessionCreateOptions
        //    {
        //        PaymentMethodTypes = new List<string> { "card" },
        //        Mode = "subscription",
        //        SuccessUrl = "https://localhost:7216/success",
        //        CancelUrl = "https://localhost:7216/cancel",
        //        LineItems = new List<SessionLineItemOptions>
        //{
        //    new SessionLineItemOptions
        //    {
        //        Price = request.PriceId,
        //        Quantity = 1,
        //    },
        //},
        //        ClientReferenceId = request.UserId.ToString(),
        //        Metadata = new Dictionary<string, string>
        //{
        //    { "Plan", request.PriceId }
        //}
        //    };

        //    // 👇 Add this: reuse existing Stripe Customer
        //    var existingCustomer = await _context.StripeSubscription
        //        .Where(x => x.UserId == request.UserId)
        //        .OrderByDescending(x => x.StartDate)
        //        .Select(x => x.StripeCustomerId)
        //        .FirstOrDefaultAsync();

        //    if (!string.IsNullOrEmpty(existingCustomer))
        //    {
        //        options.Customer = existingCustomer;
        //    }

        //    var service = new SessionService();
        //    var session = await service.CreateAsync(options);

        //    return Ok(new { url = session.Url });
        //}

        //[HttpPost("create-payment-intent")]
        //public async Task<IActionResult> CreatePaymentIntent([FromBody] PaymentIntentRequest request)
        //{
        //    var options = new PaymentIntentCreateOptions
        //    {
        //        Amount = (long)(request.Amount * 100), // amount in cents (e.g. $199 → 19900)
        //        Currency = "usd",
        //        AutomaticPaymentMethods = new PaymentIntentAutomaticPaymentMethodsOptions
        //        {
        //            Enabled = true,
        //        },
        //        Metadata = new Dictionary<string, string>
        //{
        //    { "userId", request.UserId },
        //    { "plan", request.PlanName }
        //}
        //    };

        //    var service = new PaymentIntentService();
        //    var paymentIntent = await service.CreateAsync(options);

        //    return Ok(new { clientSecret = paymentIntent.ClientSecret });
        //}

        //public class PaymentIntentRequest
        //{
        //    public string UserId { get; set; }
        //    public string PlanName { get; set; }
        //    public decimal Amount { get; set; } // USD
        //}

        // 🔹 Stripe Webhook endpoint
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
                Console.WriteLine($"⚠️ Stripe webhook signature error: {ex.Message}");
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
                case "checkout.session.completed":
                    await _stripeRepository.HandleCheckoutCompletedAsync(stripeEvent);
                    break;

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
                    Console.WriteLine($"⚙️ Unhandled event type: {stripeEvent.Type}");
                    break;
            }

            return Ok();
        }
    }
}

    // 🔹 Request model for checkout
    public class CheckoutRequest
    {
        public string PriceId { get; set; } = string.Empty;
        public string UserId { get; set; } = string.Empty;
    }

