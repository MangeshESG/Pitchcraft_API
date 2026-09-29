using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PitchGenApi.Interfaces;
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
        private readonly ISuperAdminGuard _superAdminGuard;
        private readonly string _webhookSecret;

        public StripeController(
            IConfiguration config,
            IStripeRepository stripeRepository,
            AppDbContext context,
            ISuperAdminGuard superAdminGuard)
        {
            _stripeRepository = stripeRepository;
            _webhookSecret = config["Stripe:WebhookSecret"];
            StripeConfiguration.ApiKey = config["Stripe:SecretKey"];
            _context = context;
            _superAdminGuard = superAdminGuard;
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

        /// <summary>
        /// The plan ids that hand out credit by the number rather than by a
        /// Stripe price. "Custom Credit" is what a paid credit purchase
        /// writes; the opaque one is a grant made by an admin, stored as
        /// "Internal" so it is distinguishable in a client's plan history.
        /// They spend identically.
        /// </summary>
        private static bool IsGrantPlan(string? planId) =>
            planId == "Custom Credit" ||
            planId == "Jdnkdomd8585dkbsdhhnLNDKmm4&^^588400755%";

        /// <summary>
        /// Grants credits to a client (Settings &gt; Admin &gt; Credits).
        ///
        /// This mints balance out of nothing, so it is limited to the client
        /// ids listed under <c>SuperAdmins:ClientIds</c> rather than to admins
        /// in general.
        /// </summary>
        [Authorize]
        [HttpPost("save-user-credits")]
        public async Task<IActionResult> SaveUserCredits([FromBody] SaveUserCreditsRequest request)
        {
            var check = await _superAdminGuard.AuthorizeAsync(User);

            if (!check.IsAllowed)
                return StatusCode(check.StatusCode, new { success = false, message = check.Message });

            if (request == null || request.UserId <= 0)
                return BadRequest(new { success = false, message = "A client is required." });

            if (string.IsNullOrWhiteSpace(request.PlanId))
                return BadRequest(new { success = false, message = "A plan is required." });

            // The two grant plans take their amount from CreditsCount rather
            // than from a price, so a missing one has to be caught here. A null
            // would not just write an empty row: SaveUserCreditsAsync adds it
            // to CustomLimit, and int? + null is null, which would blank out
            // whatever custom credit the client already had.
            if (IsGrantPlan(request.PlanId) && (request.CreditsCount ?? 0) <= 0)
                return BadRequest(new { success = false, message = "Enter how many credits to add." });

            var target = await _context.ClientDetails
                .AsNoTracking()
                .FirstOrDefaultAsync(client => client.Id == request.UserId);

            if (target == null)
                return NotFound(new { success = false, message = "Client not found." });

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

                Console.WriteLine(
                    $"✅ Admin {check.CallerId} added {request.CreditsCount} credits " +
                    $"({request.PlanId}) to ClientId {request.UserId}");

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

        /// <summary>
        /// Takes credits back off a client (Settings &gt; Admin &gt; Credits),
        /// for a grant made to the wrong account or a payment refunded in
        /// Stripe. Same allowlist as adding them.
        /// </summary>
        [Authorize]
        [HttpPost("admin/reduce-user-credits")]
        public async Task<IActionResult> ReduceUserCredits([FromBody] ReduceUserCreditsRequest request)
        {
            var check = await _superAdminGuard.AuthorizeAsync(User);

            if (!check.IsAllowed)
                return StatusCode(check.StatusCode, new { success = false, message = check.Message });

            if (request == null || request.UserId <= 0)
                return BadRequest(new { success = false, message = "A client is required." });

            if (request.CreditsCount <= 0)
                return BadRequest(new { success = false, message = "Enter how many credits to remove." });

            var target = await _context.ClientDetails
                .AsNoTracking()
                .FirstOrDefaultAsync(client => client.Id == request.UserId);

            if (target == null)
                return NotFound(new { success = false, message = "Client not found." });

            try
            {
                var result = await _stripeRepository.ReduceUserCreditsAsync(
                    request.UserId,
                    request.CreditsCount,
                    request.Reason);

                // "They do not have that many" is the admin's answer to read,
                // not a server fault — 400 rather than 500.
                if (!result.Success)
                    return BadRequest(result);

                Console.WriteLine(
                    $"✅ Admin {check.CallerId} removed {request.CreditsCount} credits " +
                    $"from ClientId {request.UserId}. Reason: {request.Reason ?? "not given"}");

                return Ok(result);
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