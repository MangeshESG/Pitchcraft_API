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

        [HttpGet("login")]
        public async Task<IActionResult> Login(int clientId, string SenderName)
        {
            var url = await _repo.GmailGetAuthUrlAsync(clientId, SenderName);
            return Redirect(url);
        }

        [HttpGet("callback")]
        public async Task<IActionResult> Callback(string code, string state)
        {
            // 🔥 decode state
            var parts = Uri.UnescapeDataString(state).Split('|');

            int clientId = int.Parse(parts[0]);
            string senderName = parts.Length > 1 ? parts[1] : "";

            var result = await _repo.GmailHandleCallbackAsync(code, clientId, senderName);

            var html = @"
                <html>
                <body>
                    <script>
                        window.opener.postMessage('gmail-connected', '*');
                        window.close();
                    </script>
                    <p>Gmail Connected. You can close this window.</p>
                </body>
                </html>";

            return Content(html, "text/html");
        }
    }
}
