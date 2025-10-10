using Microsoft.AspNetCore.Mvc;
using PitchGenApi.Database;
using PitchGenApi.Model.DTOs;
using PitchGenApi.Services;
using PitchGenApi.Model;
using System.Text;
using System.Security.Cryptography;

namespace PitchGenApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class PlaneController : ControllerBase
    {
        private readonly ZohoService _zohoService;
        private readonly IConfiguration _configuration;
        private readonly AppDbContext _context;
        private const string ZohoSecretKey = "ijdfhumsjjjewkss447dom-0MKODFOOE9MFC"; // Set this same key in Zoho dashboard

        public PlaneController(ZohoService zohoService, IConfiguration configuration, AppDbContext context)
        {
            _zohoService = zohoService;
            _configuration = configuration;
            _context = context;
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

        [HttpPost("receive")]
        public async Task<IActionResult> ReceivePaymentWebhook()
        {
            try
            {
                // 🔹 Step 1: Read raw request body
                using var reader = new StreamReader(Request.Body, Encoding.UTF8);
                string rawBody = await reader.ReadToEndAsync();

                // 🔹 Step 2: Verify Zoho signature
                if (!Request.Headers.TryGetValue("X-Zoho-Signature", out var signatureHeader))
                {
                    return BadRequest(new { message = "Missing X-Zoho-Signature header" });
                }

                using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(ZohoSecretKey));
                byte[] hashBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(rawBody));
                string computedSignature = Convert.ToBase64String(hashBytes);

                if (computedSignature != signatureHeader)
                {
                    return BadRequest(new { message = "Invalid signature" });
                }

                // 🔹 Step 3: Save full payload to DB
                await _context.WebhookLogs.AddAsync(new WebhookLogs
                {
                    EventName = "Zoho Payment Webhook",
                    JsonData = rawBody,
                    CreatedAt = DateTime.UtcNow
                });
                await _context.SaveChangesAsync();

                // 🔹 Step 4: Return success to Zoho
                return Ok(new { message = "Webhook received and verified successfully" });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Webhook error: {ex.Message}");
                return BadRequest(new { message = "Error processing webhook" });
            }
        }
    }

}

