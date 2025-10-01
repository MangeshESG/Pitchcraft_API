using Newtonsoft.Json;
using System.Net.Http;
using System.Net;

namespace PitchGenApi.Services
{
    public class PlaneServices
    {
        private readonly IConfiguration _configuration;
        private readonly HttpClient _httpClient;

        public PlaneServices(IConfiguration configuration, HttpClient httpClient)
        {
            _configuration = configuration;
            _httpClient = httpClient;
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
