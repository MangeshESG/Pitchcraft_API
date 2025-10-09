using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using PitchGenApi.Database;
using PitchGenApi.Model.DTOs;
using PitchGenApi.Services;
using System.Text;
using System.Text.Json;

namespace PitchGenApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class PlaneController : ControllerBase
    {
        private readonly ZohoService _zohoService;
        private readonly IConfiguration _configuration;

        public PlaneController(ZohoService zohoService, IConfiguration configuration)
        {
            _zohoService = zohoService;
            _configuration = configuration;
        }

        [HttpPost("new-subscription")]
        public async Task<IActionResult> CreateNewSubscription([FromQuery] int clientId, [FromBody] ZohoSubscriptionRequest requestModel)
        {
            try
            {
                var result = await _zohoService.CreateNewSubscription(requestModel, clientId);
                return Ok(result);
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }
        [HttpGet("get-Customers")]
        public async Task<IActionResult> GetCustomers([FromQuery] int clientId)
        {
            try
            {
                var result = await _zohoService.GetCustomers(clientId);
                return Ok(result);
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }

        [HttpPost("payment-webhook")]
        public async Task<IActionResult> PaymentWebhook([FromBody] JsonElement payload)
        {
            // ✅ Step 1: Verify Secret Header
            if (!Request.Headers.TryGetValue("X-Zoho-Auth-Token", out var token) || token != _configuration["Zoho:WebhookSecret"])
                return Unauthorized(new { message = "Invalid or missing Zoho secret" });

            // ✅ Step 2: Process Payload
            Console.WriteLine("Zoho Webhook received: " + payload.GetRawText());
            // ... handle payment success/failure logic here ...

            return Ok(new { message = "Processed successfully" });
        }
    }
}
