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
using PitchGenApi.Model;
using Newtonsoft.Json.Linq;

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
                if (!Request.Headers.TryGetValue("X-Zoho-Signature", out var signatureHeader))
                {
                    return BadRequest(new { message = "Missing X-Zoho-Signature header" });
                }

                string ZohoSecretKey = "ijdfhumsjjjewkss447dom-0MKODFOOE9MFC"; // same as in Zoho webhook settings

                // Step 3: Compute hash
                using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(ZohoSecretKey));
                byte[] hashBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(rawBody));
                string computedSignature = Convert.ToBase64String(hashBytes);

                // Step 4: Compare signature
                if (ZohoSecretKey != signatureHeader)
                {
                    return BadRequest(new { message = "Invalid signature" });
                }

                // Step 5: Deserialize payload
                var payload = JsonConvert.DeserializeObject<JObject>(rawBody);

                // Extract customer_id
                string customerId = payload["payment"]?["customer_id"]?.ToString();

                // Extract subscription_ids from invoices array
                var subscriptionIds = payload["payment"]?["invoices"]?
                                         .SelectMany(inv => inv["subscription_ids"] ?? new JArray())
                                         .Select(s => s.ToString())
                                         .ToList();

                // Now you have variables
                Console.WriteLine($"Customer ID: {customerId}");
                Console.WriteLine("Subscription IDs:");
                subscriptionIds.ForEach(Console.WriteLine);

                // Step 6: Save raw payload in DB
                await _context.WebhookLogs.AddAsync(new WebhookLogs
                {
                    EventName = "Zoho Payment Webhook",
                    JsonData = rawBody,
                    CreatedAt = DateTime.UtcNow
                });
                await _context.SaveChangesAsync();

                var planDetailsList = new List<(string CustomerId, string PlanName)>();

                foreach (var subscriptionId in subscriptionIds)
                {
                    var planDetails = await _zohoService.GetSubscriptionDetails(subscriptionId);
                    planDetailsList.Add(planDetails);

                    var Customer = _context.ZohoCustomer
                        .FirstOrDefault(c => c.CustomerId == planDetails.CustomerId);

                    if (Customer != null)
                    {
                        await _context.UserCredits.AddAsync(new UserCredits
                        {
                            ClientId = Customer.ClientId,
                            Credits = 152,
                            CreatedAt = DateTime.UtcNow
                        });
                        await _context.SaveChangesAsync();
                    }
                }
                return Ok(new { message = "Webhook received and verified successfully" });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Webhook error: {ex.Message}");
                return BadRequest(new { message = "Error processing webhook", error = ex.Message });
            }
        }

        [HttpGet("subscription-details")]
        public async Task<IActionResult> GetSubscriptionDetails([FromQuery] string subscriptionId)
        {
            if (string.IsNullOrEmpty(subscriptionId))
            {
                return BadRequest(new
                {
                    success = false,
                    message = "subscriptionId parameter is required"
                });
            }

            try
            {
                var result = await _zohoService.GetSubscriptionDetails(subscriptionId);
                return Ok(new
                {
                    success = true,
                    customerId = result.CustomerId,
                    planName = result.PlanName
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new
                {
                    success = false,
                    message = ex.Message
                });
            }
        }


        [HttpGet("test-tls-direct")]
        public async Task<IActionResult> TestTlsDirect()
        {
            try
            {
                // Test with explicit TLS 1.2 handler
                var handler = new System.Net.Http.HttpClientHandler
                {
                    SslProtocols = System.Security.Authentication.SslProtocols.Tls12 |
                                  System.Security.Authentication.SslProtocols.Tls13
                };

                using var client = new HttpClient(handler);

                // Test 1: TLS test endpoint
                var tlsTestResponse = await client.GetAsync("https://tlstest.zoho.com/api");
                var tlsTestContent = await tlsTestResponse.Content.ReadAsStringAsync();

                // Test 2: Billing API endpoint (same as your CreateCustomer)
                var refreshToken = _configuration["Zoho:RefreshToken"];
                string clientID = _configuration["Zoho:ClientId"];
                string clientSecret = _configuration["Zoho:ClientSecret"];

                var tokenUrl = "https://accounts.zoho.com/oauth/v2/token";
                var tokenParams = new Dictionary<string, string>
        {
            { "refresh_token", refreshToken },
            { "client_id", clientID },
            { "client_secret", clientSecret },
            { "grant_type", "refresh_token" }
        };

                var tokenRequest = new HttpRequestMessage(HttpMethod.Post, tokenUrl);
                tokenRequest.Content = new FormUrlEncodedContent(tokenParams);
                var tokenResponse = await client.SendAsync(tokenRequest);
                var tokenContent = await tokenResponse.Content.ReadAsStringAsync();

                string accessToken = null;
                if (tokenResponse.IsSuccessStatusCode)
                {
                    dynamic tokenJson = Newtonsoft.Json.JsonConvert.DeserializeObject(tokenContent);
                    accessToken = tokenJson["access_token"];
                }

                // Test 3: Actual Billing API call
                string billingTestResult = "Not tested";
                if (!string.IsNullOrEmpty(accessToken))
                {
                    string organizationId = _configuration["Zoho:OrganizationId"];
                    var billingUrl = "https://www.zohoapis.com/billing/v1/customers";

                    var billingRequest = new HttpRequestMessage(HttpMethod.Get, billingUrl);
                    billingRequest.Headers.Add("Authorization", $"Zoho-oauthtoken {accessToken}");
                    billingRequest.Headers.Add("X-com-zoho-subscriptions-organizationid", organizationId);

                    var billingResponse = await client.SendAsync(billingRequest);
                    billingTestResult = $"Status: {billingResponse.StatusCode}, Content: {await billingResponse.Content.ReadAsStringAsync()}";
                }

                return Ok(new
                {
                    serverInfo = new
                    {
                        protocol = System.Net.ServicePointManager.SecurityProtocol.ToString(),
                        osVersion = Environment.OSVersion.ToString(),
                        dotnetVersion = Environment.Version.ToString(),
                        machineName = Environment.MachineName
                    },
                    tlsTest = new
                    {
                        status = tlsTestResponse.StatusCode.ToString(),
                        content = tlsTestContent
                    },
                    tokenRefresh = new
                    {
                        success = tokenResponse.IsSuccessStatusCode,
                        hasAccessToken = !string.IsNullOrEmpty(accessToken)
                    },
                    billingApiTest = billingTestResult
                });
            }
            catch (Exception ex)
            {
                return Ok(new
                {
                    success = false,
                    error = ex.Message,
                    innerError = ex.InnerException?.Message,
                    stackTrace = ex.StackTrace?.Split('\n').Take(10)
                });
            }
        }
    }

}

