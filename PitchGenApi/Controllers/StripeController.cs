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
        private readonly AppDbContext _context;
        private readonly string _webhookSecret;

        public StripeController(IConfiguration config, IStripeRepository stripeRepository, AppDbContext context)
        {
            _stripeRepository = stripeRepository;
            _webhookSecret = config["Stripe:WebhookSecret"];
            StripeConfiguration.ApiKey = config["Stripe:SecretKey"];
            _context = context;
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

        [HttpGet("active/{clientId}")]
        public async Task<IActionResult> GetActivePlanByClientId(int clientId)
        {
            var result = await _stripeRepository.GetActivePlanStatusAndPlaneAsync(clientId);

            if (result == null)
                return NotFound(new { message = "No active plan found for this client." });

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

        [HttpPost("save-user-credits")]
        public async Task<IActionResult> SaveUserCredits([FromBody] SaveUserCreditsRequest request)
        {
            try
            {
                var nextSubNumber = await _context.UserCredits.CountAsync() + 1;
                var formattedSubNumber = $"SUB-{nextSubNumber:D4}";

                await _stripeRepository.SaveUserCreditsAsync(
                    request.UserId,
                    request.PlanId,
                    null,
                    formattedSubNumber,
                    DateTime.Now,
                    null,
                    null,
                    null,
                    request.CreditsCount
                );

                return Ok(new
                {
                    success = true,
                    message = "User credits saved successfully."
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    success = false,
                    message = ex.Message
                });
            }
        }
    }
}