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
                        "latest_invoice.payments"},
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
                return BadRequest(new { error = ex.Message });
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
                    break;
            }

            return Ok();
        }
    }
}