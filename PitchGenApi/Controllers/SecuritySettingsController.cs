using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PitchGenApi.Database;
using PitchGenApi.Interfaces;

namespace PitchGenApi.Controllers
{
    /// <summary>
    /// Admin-only control panel for the application-wide security switches
    /// (Settings &gt; Security in the app).
    /// </summary>
    [ApiController]
    [Route("api/security-settings")]
    public class SecuritySettingsController : ControllerBase
    {
        private readonly ISecuritySettingsService _securitySettings;
        private readonly AppDbContext _dbContext;

        public SecuritySettingsController(
            ISecuritySettingsService securitySettings,
            AppDbContext dbContext)
        {
            _securitySettings = securitySettings;
            _dbContext = dbContext;
        }

        /// <summary>Current state of every switch the Security page renders.</summary>
        [HttpGet]
        public async Task<IActionResult> GetSettings()
        {
            try
            {
                return Ok(new
                {
                    Success = true,
                    LoginOtpEnabled = await _securitySettings.IsLoginOtpEnabledAsync()
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Success = false, Message = ex.Message });
            }
        }

        /// <summary>
        /// Turns the login OTP requirement on or off for every user. Off means
        /// username and password alone sign a user in.
        /// </summary>
        [HttpPost("login-otp")]
        public async Task<IActionResult> UpdateLoginOtp(
            [FromBody] UpdateLoginOtpRequest request)
        {
            try
            {
                if (request == null)
                    return BadRequest(new { Success = false, Message = "Request body is required." });

                // This switch weakens sign-in for the whole product, so the
                // caller has to be an admin — the UI hiding the page is not
                // enough on its own.
                var isAdmin = await _dbContext.ClientDetails
                    .AsNoTracking()
                    .Where(client => client.Id == request.UpdatedBy)
                    .Select(client => (bool?)client.IsAdmin)
                    .FirstOrDefaultAsync();

                if (isAdmin != true)
                {
                    return StatusCode(403, new
                    {
                        Success = false,
                        Message = "Only an admin can change security settings."
                    });
                }

                await _securitySettings.SetLoginOtpEnabledAsync(
                    request.Enabled,
                    request.UpdatedBy.ToString());

                return Ok(new
                {
                    Success = true,
                    Message = request.Enabled
                        ? "Login OTP verification is now required."
                        : "Login OTP verification is now off.",
                    LoginOtpEnabled = request.Enabled
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Success = false, Message = ex.Message });
            }
        }

        public class UpdateLoginOtpRequest
        {
            public bool Enabled { get; set; }

            /// <summary>Client id of the admin making the change.</summary>
            public int UpdatedBy { get; set; }
        }
    }
}
