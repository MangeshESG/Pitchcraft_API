using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using PitchGenApi.Database;
using PitchGenApi.Model;
using PitchGenApi.Model.DTOs;
using PitchGenApi.Services;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace PitchGenApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class PlaneController : ControllerBase
    {
        private readonly IConfiguration _configuration;
        private readonly PlaneServices _planeServices;
        private readonly HttpClient _httpClient;
        private readonly AppDbContext _context;

        public PlaneController(IConfiguration configuration, PlaneServices planeServices, HttpClient httpClient, AppDbContext context)
        {
            _configuration = configuration;
            _planeServices = planeServices;
            _httpClient = httpClient;
            _context = context;
        }

        [HttpGet("token")]
        public async Task<IActionResult> Token()
        {
            var refreshToken = _configuration["Zoho:RefreshToken"];
            var token = await _planeServices.RefreshToken(refreshToken);
            return Ok(token);
        }

        [HttpPost("create-customer")]
        public async Task<IActionResult> CreateCustomer([FromBody] ZohoCustomerRequest customer)
        {
            var refreshToken = _configuration["Zoho:RefreshToken"];
            var accessToken = await _planeServices.RefreshToken(refreshToken);

            string organizationId = _configuration["Zoho:OrganizationId"];

            var request = new HttpRequestMessage(HttpMethod.Post, "https://www.zohoapis.com/billing/v1/customers");
            request.Headers.Add("Authorization", $"Zoho-oauthtoken {accessToken}");
            request.Headers.Add("X-com-zoho-subscriptions-organizationid", organizationId);
            request.Headers.Add("Accept", "application/json");

            string jsonBody = JsonConvert.SerializeObject(customer, new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore
            });

            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            var responseContent = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                // Option 1: Raw JSON
                return Content(responseContent, "application/json");

                // Option 2: Deserialize into custom model
                // var customerResponse = JsonConvert.DeserializeObject<ZohoCustomerResponse>(responseContent);
                // return Ok(customerResponse);
            }

            return StatusCode((int)response.StatusCode, responseContent);
        }

        [HttpPost("create-subscription")]
        public async Task<IActionResult> CreateSubscription([FromBody] ZohoSubscriptionRequest subscriptionRequest, [FromQuery] int clientId)
        {
            try
            {
                // Step 1: Get access token
                var refreshToken = _configuration["Zoho:RefreshToken"];
                var accessToken = await _planeServices.RefreshToken(refreshToken);
                var organizationId = _configuration["Zoho:OrganizationId"];

                // Step 2: Prepare API request
                var url = "https://www.zohoapis.com/billing/v1/subscriptions";
                var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Add("Authorization", $"Zoho-oauthtoken {accessToken}");
                request.Headers.Add("X-com-zoho-subscriptions-organizationid", organizationId);
                request.Headers.Add("Accept", "application/json");

                var jsonBody = JsonConvert.SerializeObject(subscriptionRequest, new JsonSerializerSettings
                {
                    NullValueHandling = NullValueHandling.Ignore
                });
                request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

                // Step 3: Send API request
                var response = await _httpClient.SendAsync(request);
                var responseContent = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    return StatusCode((int)response.StatusCode, responseContent);
                }

                // Step 4: Parse Zoho response
                var zohoResponse = JsonConvert.DeserializeObject<ZohoSubscriptionResponse>(responseContent);

                if (zohoResponse?.Subscription == null)
                {
                    return StatusCode(500, "Invalid subscription response from Zoho.");
                }

                // Step 5: Map to DB entity
                var subscription = new Subscriptions
                {
                    SubscriptionId = long.TryParse(zohoResponse.Subscription.SubscriptionId, out var subId) ? subId : 0,
                    SubscriptionNumber = long.TryParse(zohoResponse.Subscription.SubscriptionNumber, out var subNum) ? subNum : 0,
                    CustomerId = long.TryParse(zohoResponse.Subscription.CustomerId, out var custId) ? custId : 0,
                    ProductId = long.TryParse(zohoResponse.Subscription.ProductId, out var prodId) ? prodId : 0,
                    PlanId = zohoResponse.Subscription.Plan?.PlanId,
                    CreatedAt = zohoResponse.Subscription.CreatedAt,
                    ActivatedAt = zohoResponse.Subscription.ActivatedAt,
                    CurrentTermStartsAt = zohoResponse.Subscription.CurrentTermStartsAt?.Date,
                    CurrentTermEndsAt = zohoResponse.Subscription.CurrentTermEndsAt,
                    NextBillingAt = zohoResponse.Subscription.NextBillingAt,
                    Amount = zohoResponse.Subscription.Amount,
                    SubTotal = zohoResponse.Subscription.SubTotal,
                    ClientId = clientId
                };

                // Step 6: Save to DB with error handling
                try
                {
                    _context.subscriptions.Add(subscription);
                    await _context.SaveChangesAsync();
                }
                catch (DbUpdateException dbEx)
                {
                    var innerMessage = dbEx.InnerException?.Message ?? dbEx.Message;
                    return StatusCode(500, $"Database error: {innerMessage}");
                }

                return Content(responseContent, "application/json");
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Error creating subscription: {ex.Message}");
            }
        }

    }
}
