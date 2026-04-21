using Microsoft.EntityFrameworkCore;
using PitchGenApi.Database;
using PitchGenApi.Interfaces;
using PitchGenApi.Model;
using System.Net.Http.Headers;

namespace PitchGenApi.Repository
{
    public class OAuthRepository : IOAuthRepository
    {
        private readonly AppDbContext _context;
        private readonly IConfiguration _config;

        public OAuthRepository(AppDbContext context, IConfiguration config)
        {
            _context = context;
            _config = config;
        }

        public Task<string> GmailGetAuthUrlAsync(int clientId, string SenderName)
        {
            var cfg = _config.GetSection("GoogleOAuth");

            // 🔥 pack in state
            var state = Uri.EscapeDataString($"{clientId}|{SenderName}");

            var url = $"https://accounts.google.com/o/oauth2/v2/auth?" +
                      $"client_id={cfg["ClientId"]}" +
                      $"&redirect_uri={cfg["RedirectUri"]}" +
                      $"&response_type=code" +
                      $"&scope=openid email https://www.googleapis.com/auth/gmail.readonly https://www.googleapis.com/auth/gmail.send" +
                      $"&access_type=offline" +
                      $"&prompt=consent" +
                      $"&state={state}"; // ✅ correct

            return Task.FromResult(url);
        }

        public async Task<string> GmailHandleCallbackAsync(string code, int clientId, string SenderName)
        {
            var cfg = _config.GetSection("GoogleOAuth");

            var client = new HttpClient();

            var values = new Dictionary<string, string>
        {
            { "code", code },
            { "client_id", cfg["ClientId"] },
            { "client_secret", cfg["ClientSecret"] },
            { "redirect_uri", cfg["RedirectUri"] },
            { "grant_type", "authorization_code" }
        };

            var response = await client.PostAsync(
                "https://oauth2.googleapis.com/token",
                new FormUrlEncodedContent(values));

            var json = await response.Content.ReadAsStringAsync();
            dynamic token = Newtonsoft.Json.JsonConvert.DeserializeObject(json);

            string accessToken = token.access_token;
            string refreshToken = token.refresh_token;
            int expiresIn = token.expires_in;

            // 🔥 Get Email
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

            var userInfo = await client.GetStringAsync("https://www.googleapis.com/oauth2/v2/userinfo");
            dynamic user = Newtonsoft.Json.JsonConvert.DeserializeObject(userInfo);

            string email = user.email;
            // 🔥 Save DB
            var existing = await _context.EmailOAuthTokens
                .FirstOrDefaultAsync(x => x.Email == email);

            if (existing == null)
            {
                _context.EmailOAuthTokens.Add(new EmailOAuthTokens
                {
                    Email = email,
                    Provider = "Gmail",
                    ClientId = clientId,
                    AccessToken = accessToken,
                    RefreshToken = refreshToken,
                    SenderName = SenderName,
                    ExpiryTime = DateTime.UtcNow.AddSeconds(expiresIn)
                });
            }
            else
            {
                existing.AccessToken = accessToken;
                existing.RefreshToken = refreshToken ?? existing.RefreshToken;
                existing.ExpiryTime = DateTime.UtcNow.AddSeconds(expiresIn);
            }

            await _context.SaveChangesAsync();

            return "✅ Gmail Connected";
        }

       
    }
}
