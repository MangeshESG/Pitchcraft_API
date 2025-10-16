using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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
        private readonly AppDbContext _context;
        private readonly ZohoDataService _zohodata;

        public PlaneController(ZohoService zohoService, IConfiguration configuration, AppDbContext context, ZohoDataService zohodata)
        {
            _zohoService = zohoService;
            _configuration = configuration;
            _context = context;
            _zohodata = zohodata;
        }
        [HttpPost("create-customer")]
        public async Task<IActionResult> CreateCustomer([FromQuery] int ClinteId, [FromBody] ZohoCustomerRequest customer)
        {
            try
            {
                var result = await _zohoService.CreateCustomer(customer, ClinteId);

                if (string.IsNullOrEmpty(result))
                {
                    return BadRequest(new { message = "Something went wrong" });
                }
                else
                {
                    return Ok(new { customer_id = result });
                }
            }
            catch (Exception ex)
            {
                // Optionally log the exception
                return BadRequest(new { error = ex.Message });
            }
        }

        [HttpGet("get-Countries")]
        public async Task<IActionResult> GetAllCountries()
        {
            var countries = await _context.Countriesdropdown
                .Select(c => new
                {
                    c.Id,
                    c.Name,
                    c.Currency
                })
                .ToListAsync();

            return Ok(countries);
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
                var result = await _zohodata.GetCustomers(clientId);
                return Ok(result);
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }
        
        [HttpGet("get-CustomersInClient")]
        public async Task<IActionResult> GetCustomersInClient([FromQuery] int clientId)
        {
            try
            {
                var result = await _zohodata.GetCustomersInClient(clientId);
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
