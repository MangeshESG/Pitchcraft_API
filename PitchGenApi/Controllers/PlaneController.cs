using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using PitchGenApi.Database;
using PitchGenApi.Model.DTOs;
using PitchGenApi.Services;
using System.ComponentModel.DataAnnotations.Schema;
using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
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
                // Step 1: Read raw body
                using var reader = new StreamReader(Request.Body, Encoding.UTF8);
                string rawBody = await reader.ReadToEndAsync();

                // Step 2: Get Zoho signature
                if (!Request.Headers.TryGetValue("X-Zoho-Signature",out var signatureHeader))
                {
                    return BadRequest(new { message = "Missing X-Zoho-Signature header" });
                }

                string ZohoSecretKey = "ijdfhumsjjjewkss447dom-0MKODFOOE9MFC"; // same as in Zoho webhook settings

                // Step 3: Compute hash (Hex)
                using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(ZohoSecretKey));
                byte[] hashBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(rawBody));

                // Step 4: Compare
                if (ZohoSecretKey != signatureHeader)
                {
                    return BadRequest(new { message = "Invalid signature" });
                }

                // Step 5: Save payload in DB
                await _context.WebhookLogs.AddAsync(new WebhookLogs
                {
                    EventName = "Zoho Payment Webhook",
                    JsonData = rawBody,
                    CreatedAt = DateTime.UtcNow
                });
                await _context.SaveChangesAsync();

                return Ok(new { message = "Webhook received and verified successfully" });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Webhook error: {ex.Message}");
                return BadRequest(new { message = "Error processing webhook" });
            }
        }
        public class WebhookLogs
        {
            [Key]
            public int Id { get; set; }
            public string? EventName { get; set; }
            public string? JsonData { get; set; }
            public DateTime? CreatedAt { get; set; }
        }
    }

}

