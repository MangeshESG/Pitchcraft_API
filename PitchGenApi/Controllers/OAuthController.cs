using Microsoft.AspNetCore.Authorization;
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
        public async Task<IActionResult> GmailLogin(int clientId, string SenderName, bool FullInboxSync)
        {
            var url = await _repo.GmailGetAuthUrlAsync(clientId, SenderName, FullInboxSync);
            return Redirect(url);
        }

        [HttpGet("test")]
        [AllowAnonymous]
        public IActionResult Test()
        {
            return Ok("WORKING");
        }

        [HttpGet("Gmail_callback")]
        [AllowAnonymous]
        public async Task<IActionResult> GmailCallback([FromQuery]string code, [FromQuery] string state)
        {
            try
            {
                // Ensure 'code' is not empty
                if (string.IsNullOrEmpty(code))
                {
                    return Content("<h3>Authorization failed. No code received.</h3>", "text/html");
                }

                // Ensure 'state' is not empty and decode it safely
                if (string.IsNullOrEmpty(state))
                {
                    return Content("<h3>Authorization failed. No state received.</h3>", "text/html");
                }

                // Decode and parse the state
                var parts = Uri.UnescapeDataString(state).Split('|');

                if (parts.Length < 1)
                {
                    return Content("<h3>Invalid state received. Could not parse state.</h3>", "text/html");
                }

                // Safely parse clientId (use TryParse to prevent exception on invalid format)
                if (!int.TryParse(parts[0], out int clientId))
                {
                    return Content("<h3>Invalid client ID received in state.</h3>", "text/html");
                }

                string senderName = parts.Length > 1 ? parts[1] : "";
                bool fullInboxSync = parts.Length > 2 && bool.TryParse(parts[2], out bool fis) && fis;

                // Call repository method to handle Gmail OAuth callback
                var result = await _repo.GmailHandleCallbackAsync(code, clientId, senderName, fullInboxSync);

                // Check if Gmail is successfully connected
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

                // Failure case
                var failHtml = $@"
        <html>
        <body style='font-family:Arial'>
            <h3 style='color:red;'>❌ {result}</h3>
            <p>Please try again.</p>
        </body>
        </html>";

                return Content(failHtml, "text/html");
            }
            catch (Exception ex)
            {
                // Catch unexpected errors and display them
                var errorHtml = $@"
        <html>
        <body style='font-family:Arial'>
            <h3 style='color:red;'>❌ Error: {ex.Message}</h3>
            <p>Stack Trace: {ex.StackTrace}</p>
        </body>
        </html>";

                return Content(errorHtml, "text/html");
            }
        }

        [HttpGet("Outlook_login")]
        public async Task<IActionResult> Login(int clientId, string SenderName, bool FullInboxSync)
        {
            var url = await _repo.OutlookGetAuthUrlAsync(clientId, SenderName, FullInboxSync);
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
                bool fullInboxSync = parts.Length > 2 && bool.TryParse(parts[2], out bool fis) && fis;

                var result = await _repo.OutlookHandleCallbackAsync(code, clientId, senderName, fullInboxSync);

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
