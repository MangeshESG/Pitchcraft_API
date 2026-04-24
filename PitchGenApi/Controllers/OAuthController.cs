using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PitchGenApi.Interfaces;

namespace PitchGenApi.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class OAuthController : ControllerBase
    {
        private readonly IOAuthRepository _repo;

        public OAuthController(IOAuthRepository repo)
        {
            _repo = repo;
        }

        [HttpGet("Gmail_login")]
        public async Task<IActionResult> GmailLogin(int clientId, string SenderName)
        {
            var url = await _repo.GmailGetAuthUrlAsync(clientId, SenderName);
            return Redirect(url);
        }

        [HttpGet("Gmail_callback")]
        public async Task<IActionResult> GmailCallback(string code, string state)
        {
            try
            {
                if (string.IsNullOrEmpty(code))
                {
                    return Content("<h3> Authorization failed. No code received.</h3>", "text/html");
                }

                // 🔥 decode state
                var parts = Uri.UnescapeDataString(state).Split('|');

                int clientId = int.Parse(parts[0]);
                string senderName = parts.Length > 1 ? parts[1] : "";

                var result = await _repo.GmailHandleCallbackAsync(code, clientId, senderName);

                // 🔥 SUCCESS CASE
                if (result.Contains("Connected"))
                {
                    var successHtml = @"
            <html>
            <body>
                <script>
                    window.opener.postMessage('gmail-connected', '*');
                    window.close();
                </script>
                <p> Gmail Connected Successfully</p>
            </body>
            </html>";

                    return Content(successHtml, "text/html");
                }

                // 🔥 FAILURE CASE (IMPORTANT)
                var failHtml = $@"
        <html>
        <body style='font-family:Arial'>
            <h3 style='color:red;'>❌ {result}</h3>
            <p>try again.</p>
        </body>
        </html>";

                return Content(failHtml, "text/html");
            }
            catch (Exception ex)
            {
                var errorHtml = $@"
        <html>
        <body style='font-family:Arial'>
            <h3 style='color:red;'>❌ Error: {ex.Message}</h3>
        </body>
        </html>";

                return Content(errorHtml, "text/html");
            }
        }

        [HttpGet("Outlook_login")]
        public async Task<IActionResult> Login(int clientId, string SenderName)
        {
            var url = await _repo.OutlookGetAuthUrlAsync(clientId, SenderName);
            return Redirect(url);
        }

        [HttpGet("Outlook_callback")]
        public async Task<IActionResult> Callback(string code, string state)
        {
            try
            {
                if (string.IsNullOrEmpty(code))
                {
                    return Content("<h3> Authorization failed. No code received.</h3>", "text/html");
                }

                // 🔥 decode state
                var parts = Uri.UnescapeDataString(state).Split('|');

                int clientId = int.Parse(parts[0]);
                string senderName = parts.Length > 1 ? parts[1] : "";

                var result = await _repo.OutlookHandleCallbackAsync(code, clientId, senderName);

                // =========================
                // ✅ SUCCESS
                // =========================
                if (result.Contains("Connected"))
                {
                    var successHtml = @"
            <html>
            <body>
                <script>
                    window.opener.postMessage('outlook-connected', '*');
                    window.close();
                </script>
                <p> Outlook Connected Successfully</p>
            </body>
            </html>";

                    return Content(successHtml, "text/html");
                }

                // =========================
                // ❌ FAILURE
                // =========================
                var failHtml = $@"
        <html>
        <body style='font-family:Arial'>
            <h3 style='color:red;'> {result}</h3>
            <p>Please fix and try again.</p>
        </body>
        </html>";

                return Content(failHtml, "text/html");
            }
            catch (Exception ex)
            {
                var errorHtml = $@"
        <html>
        <body style='font-family:Arial'>
            <h3 style='color:red;'> Error: {ex.Message}</h3>
        </body>
        </html>";

                return Content(errorHtml, "text/html");
            }
        }
    }
}
