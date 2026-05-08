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

        public Task<string> GmailGetAuthUrlAsync(int clientId, string SenderName, bool FullInboxSync)
        {
            var cfg = _config.GetSection("GoogleOAuth");

            var state = Uri.EscapeDataString($"{clientId}|{SenderName}|{FullInboxSync}");

            var scopes = new[]
            {
        "openid",
        "email",
        "https://www.googleapis.com/auth/gmail.readonly",
        "https://www.googleapis.com/auth/gmail.send"
    };

            var scope = Uri.EscapeDataString(string.Join(" ", scopes)); // 🔥 FIX

            var url = $"https://accounts.google.com/o/oauth2/v2/auth?" +
                      $"client_id={cfg["ClientId"]}" +
                      $"&redirect_uri={Uri.EscapeDataString(cfg["RedirectUri"])}" + // 🔥 also encode this
                      $"&response_type=code" +
                      $"&scope={scope}" +
                      $"&access_type=offline" +
                      $"&prompt=consent" +
                      $"&state={state}";

            return Task.FromResult(url);
        }

        public async Task<string> GmailHandleCallbackAsync(string code, int clientId, string SenderName, bool FullInboxSync)
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

            // 🔥 FIX 1: Check Google response success
            if (!response.IsSuccessStatusCode)
            {
                return "Google Token Error: " + json;
            }

            dynamic token = Newtonsoft.Json.JsonConvert.DeserializeObject(json);

            // 🔥 FIX 2: Validate token before use
            if (token == null || token.access_token == null)
            {
                return "Invalid token response from Google: " + json;
            }

            string accessToken = token.access_token;
            string refreshToken = token.refresh_token;
            int expiresIn = token.expires_in;

            // =========================
            // 🔥 Get Email
            // =========================
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

            var userInfo = await client.GetStringAsync("https://www.googleapis.com/oauth2/v2/userinfo");
            dynamic user = Newtonsoft.Json.JsonConvert.DeserializeObject(userInfo);

            string email = user.email;

            // =========================
            // 🔥 CHECK SMTP EXISTS
            // =========================
            var smtp = await _context.SmtpCredentials
                .FirstOrDefaultAsync(x => x.FromEmail == email);

            if (smtp != null)
            {
                return "This email is already configured for sending (SMTP). Please configure it as an IMAP inbox instead.";
            }

            // =========================
            // 🔥 SAVE / UPDATE TOKEN
            // =========================
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
                    FullInboxSync = FullInboxSync,
                    ExpiryTime = DateTime.UtcNow.AddSeconds(expiresIn)
                });
            }
            else
            {
                existing.AccessToken = accessToken;
                existing.RefreshToken = refreshToken ?? existing.RefreshToken;
                existing.ExpiryTime = DateTime.UtcNow.AddSeconds(expiresIn);
                existing.FullInboxSync = FullInboxSync;
            }

            await _context.SaveChangesAsync();

            return "Connected";
        }

        public Task<string> OutlookGetAuthUrlAsync(int clientId, string senderName, bool FullInboxSync)
        {
            var cfg = _config.GetSection("MicrosoftOAuth");

            var scope = Uri.EscapeDataString(
                "openid email offline_access " +
                "https://graph.microsoft.com/User.Read " +
                "https://graph.microsoft.com/Mail.ReadWrite " +
                "https://graph.microsoft.com/Mail.Send"
            );

            var url =
                $"https://login.microsoftonline.com/{cfg["TenantId"]}/oauth2/v2.0/authorize?" +
                $"client_id={cfg["ClientId"]}" +
                $"&response_type=code" +
                $"&redirect_uri={Uri.EscapeDataString(cfg["RedirectUri"])}" +
                $"&response_mode=query" +
                $"&scope={scope}" +
                $"&prompt=consent" +
                $"&state={clientId}|{senderName}|{FullInboxSync}";

            return Task.FromResult(url);
        }

        public async Task<string> OutlookHandleCallbackAsync(string code, int clientId, string SenderName, bool FullInboxSync)
        {
            
            var cfg = _config.GetSection("MicrosoftOAuth");

            var client = new HttpClient();

            var values = new Dictionary<string, string>
            {
                { "client_id", cfg["ClientId"] },
                { "client_secret", cfg["ClientSecret"] },
                { "code", code },
                { "redirect_uri", cfg["RedirectUri"] },
                { "grant_type", "authorization_code" }
            };

            var response = await client.PostAsync(
        $"https://login.microsoftonline.com/{cfg["TenantId"]}/oauth2/v2.0/token",
                new FormUrlEncodedContent(values));

            var json = await response.Content.ReadAsStringAsync();
            dynamic token = Newtonsoft.Json.JsonConvert.DeserializeObject(json);

            string accessToken = token.access_token;
            string refreshToken = token.refresh_token;
            int expiresIn = token.expires_in;

            // 🔥 GET EMAIL
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", accessToken);

            var userInfo = await client.GetStringAsync("https://graph.microsoft.com/v1.0/me");
            dynamic user = Newtonsoft.Json.JsonConvert.DeserializeObject(userInfo);

            string email = user.mail ?? user.userPrincipalName;

            // 🔥 SAVE DB
            var existing = await _context.EmailOAuthTokens
                .FirstOrDefaultAsync(x => x.Email == email);
            var smtp = await _context.SmtpCredentials
               .FirstOrDefaultAsync(x => x.FromEmail == email);

            if (smtp != null)
            {
                return "This email is already configured for sending (SMTP). Please configure it as an IMAP inbox instead.";
            }
            if (existing == null)
            {
                _context.EmailOAuthTokens.Add(new EmailOAuthTokens
                {
                    Email = email,
                    Provider = "Outlook",
                    ClientId = clientId,
                    AccessToken = accessToken,
                    RefreshToken = refreshToken,
                    SenderName = SenderName,
                    FullInboxSync = FullInboxSync,
                    ExpiryTime = DateTime.UtcNow.AddSeconds(expiresIn),
                    CreatedAt = DateTime.UtcNow
                });
            }
            else
            {
                existing.AccessToken = accessToken;
                existing.RefreshToken = refreshToken ?? existing.RefreshToken;
                existing.ExpiryTime = DateTime.UtcNow.AddSeconds(expiresIn);
                existing.FullInboxSync = FullInboxSync;
            }

            await _context.SaveChangesAsync();

            return "✅ Outlook Connected Successfully";
        }
    }
}
