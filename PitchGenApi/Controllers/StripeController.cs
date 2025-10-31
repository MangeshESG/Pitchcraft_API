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
        private readonly IStripeRepository _stripeRepository;
        private readonly string _webhookSecret;

        public StripeController(IConfiguration config, IStripeRepository stripeRepository)
        {
            _stripeRepository = stripeRepository;
            _webhookSecret = config["Stripe:WebhookSecret"];
            StripeConfiguration.ApiKey = config["Stripe:SecretKey"];
        }

        [HttpPost("create-credit-intent")]
        public async Task<IActionResult> CreateCreditIntent([FromQuery] string UserId, [FromQuery] int Credits)
        {
            try
            {
                var clientSecret = await _stripeRepository.CreateCreditPurchaseIntentAsync(UserId, Credits);
                return Ok(new { clientSecret });
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }

        [HttpGet("get-user-plan_history")]
        public async Task<IActionResult> GetUserCredits(int clientId, int pageNumber = 1, int pageSize = 10)
        {
            var result = await _stripeRepository.GetPlanHistoryByClientIdAsync(clientId, pageNumber, pageSize);
            return Ok(result);
        }

        [HttpPost("create-subscription")]
        public async Task<IActionResult> CreateSubscription([FromBody] CreateSubscriptionRequest req)
        {
            var result = await _stripeRepository.CreateSubscriptionAsync(req);
            return Ok(new
            {
                subscriptionNumber = result.SubscriptionNumber,
                subscriptionId = result.SubscriptionId,
                clientSecret = result.ClientSecret
            });
        }

        [HttpGet("{invoiceId}")]
        public async Task<IActionResult> GetInvoiceUrls(string invoiceId)
        {
            var invoice = await _stripeRepository.GetInvoiceDetailsAsync(invoiceId);
            return Ok(new
            {
                InvoiceDwtils = invoice
            });
        }

        //[HttpGet("customer/{ClientId}/subscriptions")]
        //public async Task<IActionResult> GetAllSubscriptionsByCustomer(
        //string ClientId,
        //int limit = 10,
        //string? startingAfter = null)
        //{
        //    var result = await _stripeRepository.GetAllSubscriptionsByCustomerAsync(ClientId, limit, startingAfter);
        //    return Ok(new
        //    {
        //        Data = result.Data,
        //        HasMore = result.HasMore,
        //        NextCursor = result.NextCursor
        //    });
        //}

        [HttpPost("webhook")]
        public async Task<IActionResult> Webhook()
        {
            var json = await new StreamReader(HttpContext.Request.Body).ReadToEndAsync();
            var stripeEvent = EventUtility.ConstructEvent(
                json,
                Request.Headers["Stripe-Signature"],
                _webhookSecret
            );

            await _stripeRepository.HandleWebhookEventAsync(stripeEvent);
            return Ok();
        }
    }
}