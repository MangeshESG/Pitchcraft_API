using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PitchGenApi.Database;
using PitchGenApi.Model;
using PitchGenApi.Model.DTOs;
using PitchGenApi.Models.DTOs;
using PitchGenApi.Services;
using System.Net;
using System.Text;
using System.Text.Json;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace PitchGenApi
{
    public class ZohoSubscriptionService
    {
        private readonly IConfiguration _configuration;
        private readonly HttpClient _httpClient;
        private readonly AppDbContext _Context;

        public ZohoSubscriptionService(IConfiguration configuration, HttpClient httpClient, AppDbContext appDbContext)
        {
            _configuration = configuration;
            _httpClient = httpClient;
            _Context = appDbContext;
        }


        public async Task<ZohoHostedPageResponse.HostedPage> CreateNewSubscriptionAsync(ZohoSubscriptionRequest requestModel, int clientId)
        {
            var refreshToken = _configuration["Zoho:RefreshToken"];
            var accessToken = await RefreshToken(refreshToken);

            string organizationId = _configuration["Zoho:OrganizationId"];
            var url = "https://www.zohoapis.com/billing/v1/hostedpages/newsubscription";

            string jsonBody = JsonSerializer.Serialize(requestModel);

            var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("Authorization", $"Zoho-oauthtoken {accessToken}");
            request.Headers.Add("X-com-zoho-subscriptions-organizationid", organizationId);
            request.Content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var responseContent = await response.Content.ReadAsStringAsync();

            var hostedPageResponse = JsonSerializer.Deserialize<ZohoHostedPageResponse>(responseContent);

            var entity = new Subscription
            {
                ClientId = clientId,
                CustomerId = requestModel.customer_id,
                PlanCode = requestModel.plan.plan_code,
                CustomerName = requestModel.customer.display_name,
                Email = requestModel.customer.email,
                Quantity = requestModel.plan.quantity,
                Payment_Gateway = requestModel.payment_gateways.FirstOrDefault()?.payment_gateway,
                Createdat = DateTime.UtcNow
            };

            _Context.Subscription.Add(entity);
            await _Context.SaveChangesAsync();

            return hostedPageResponse.hostedpage;
        }

        public async Task<string> CreateCustomerAsync(ZohoCustomerRequest customer, int clientId)
        {
            var refreshToken = _configuration["Zoho:RefreshToken"];
            var accessToken = await RefreshToken(refreshToken);

            string organizationId = _configuration["Zoho:OrganizationId"];
            var url = "https://www.zohoapis.com/billing/v1/customers";

            var request = new HttpRequestMessage(HttpMethod.Post, url);
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

            var customerData = JsonSerializer.Deserialize<ZohoCustomerResponse>(responseContent);
            if (customerData.Customer != null)
            {
                var entity = new ZohoCustomer
                {
                    ClientId = clientId,
                    CustomerId = customerData.Customer.CustomerId,
                    ContactId = customerData.Customer.ContactId,
                    PrimaryContactPersonId = customerData.Customer.PrimaryContactPersonId,
                    DisplayName = customerData.Customer.DisplayName,
                    ContactName = customerData.Customer.ContactName,
                    FirstName = customerData.Customer.FirstName,
                    LastName = customerData.Customer.LastName,
                    Email = customerData.Customer.Email,
                    Mobile = customerData.Customer.Mobile,
                    Status = customerData.Customer.Status,
                    Createdat = DateTime.UtcNow
                };
                _Context.ZohoCustomer.Add(entity);
                await _Context.SaveChangesAsync();
                return customerData.Customer.CustomerId;
            }

            return null;
        }

        public async Task<string> RefreshToken(string refreshToken)
        {
            try
            {
                string clientID = _configuration["Zoho:ClientId"];
                string clientSecret = _configuration["Zoho:ClientSecret"];

                Console.WriteLine($"Attempting to refresh token...");
                Console.WriteLine($"Client ID: {clientID?.Substring(0, Math.Min(10, clientID?.Length ?? 0))}...");
                Console.WriteLine($"Has refresh token: {!string.IsNullOrEmpty(refreshToken)}");

                var url = "https://accounts.zoho.com/oauth/v2/token";
                var parameters = new Dictionary<string, string>
                {
                    { "refresh_token", refreshToken },
                    { "client_id", clientID },
                    { "client_secret", clientSecret },
                    { "grant_type", "refresh_token" }
                };

                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Content = new FormUrlEncodedContent(parameters);

                var response = await _httpClient.SendAsync(request);
                var responseContent = await response.Content.ReadAsStringAsync();

                Console.WriteLine($"Token refresh response status: {response.StatusCode}");

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    dynamic responseJson = JsonConvert.DeserializeObject(responseContent);
                    string accessToken = responseJson["access_token"];
                    Console.WriteLine($"Successfully refreshed access token: {accessToken?.Substring(0, Math.Min(20, accessToken?.Length ?? 0))}...");
                    return accessToken;
                }
                else
                {
                    Console.WriteLine($"Failed to refresh token. Response: {responseContent}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error refreshing token: {ex.Message}");
                Console.WriteLine($"Stack trace: {ex.StackTrace}");
            }
            return string.Empty;
        }
    }
}
