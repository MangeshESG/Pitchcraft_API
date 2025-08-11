using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using PitchGenApi.Database;
using PitchGenApi.Helpers;
using PitchGenApi.Interfaces;
using PitchGenApi.Model;
using PitchGenApi.Model.DTOs;
using PitchGenApi.Repository;
using PitchGenApi.Services;
using System.Security.Cryptography;
using System.Text;

namespace PitchGenApi.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class LoginController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly IUserRepository _userRepository;
        private readonly JwtService _jwtService;
        private readonly IResetPassworde _resetPassword;

        public LoginController(AppDbContext context, IUserRepository userRepository, JwtService jwtService, IResetPassworde resetPassword)
        {
            _context = context;
            _userRepository = userRepository;
            _jwtService = jwtService;
            _resetPassword = resetPassword;
        }

        [HttpPost("login")]
        public async Task<IActionResult> Login([FromForm] string username, [FromForm] string password)
        {
            var user = await _userRepository.GetUserByUsernameEmailAsync(username);
            if (user == null || !VerifyPassword(password, user.PasswordHash))
            {
                return Unauthorized(new { Message = "Invalid credentials" });
            }

            var token = _jwtService.GeneratenewToken(username, user.Id, user.FirstName.ToString(), user.LastName.ToString());

            return Ok(new
            {
                Token = token,
                //ClientID = user.ClientID,
                //Isadmin = user.IsAdmin,
                //IsDemoAccount = user.IsDemoAccount,
                //FirstName = user.FirstName,
                //LastName = user.LastName,
                //CompanyName = user.CompanyName,
            });
        }
        private bool VerifyPassword(string password, string storedPasswordHash)
        {
            using (var sha256 = SHA256.Create())
            {
                byte[] inputBytes = Encoding.UTF8.GetBytes(password);
                byte[] hashBytes = sha256.ComputeHash(inputBytes);
                string hashOfInput = Convert.ToBase64String(hashBytes);

                return hashOfInput == storedPasswordHash;
            }
        }

        // Step 1: Register Request → Generate OTP → Send Email
        [HttpPost("register")]
        public IActionResult Register([FromBody] RegisterRequest request)
        {
            if (_context.ClientDetails.Any(u => u.Email == request.Email || u.Username == request.Username))
            {
                return BadRequest("Email or Username already exists.");
            }

            // Generate OTP
            string otp = OtpGenerator.GenerateSecureOtp();  // default 6-character

            // Save OTP
            var otpEntity = new EmailOtpVerification
            {
                Email = request.Email,
                OTP = otp,
                IsVerified = false,
                CreatedAt = DateTime.Now,
                ExpiresAt = DateTime.Now.AddMinutes(10)
            };
            _context.EmailOtpVerifications.Add(otpEntity);
            _context.SaveChanges();

            // Send OTP
            var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();

            // Get browser user-agent
            var userAgent = Request.Headers["User-Agent"].ToString();
            var browserName = EmailTrackingHelper.GetBrowserName(userAgent);

            // Send OTP email
            RegisterEmailSender.SendOtpEmail(request.Email, otp, request.FirstName, ipAddress, browserName);
            // Temporarily store registration details in TempData or in-memory (you can use Redis for production)
            HttpContext.Session.SetString(request.Email, JsonConvert.SerializeObject(request));

            return Ok("OTP sent to your email.");
        }

        // Step 2: Verify OTP and Save ClientDetails
        [HttpPost("verify-otp")]
        public IActionResult VerifyOtp([FromBody] VerifyOtpRequest request)
        {
            var otpRecord = _context.EmailOtpVerifications
                .FirstOrDefault(o => o.Email == request.Email && o.OTP == request.Otp);

            if (otpRecord == null)
                return BadRequest("Invalid OTP.");

            if (otpRecord.ExpiresAt < DateTime.Now)
                return BadRequest("OTP expired.");

            otpRecord.IsVerified = true;
            _context.SaveChanges();

            // Retrieve stored registration data
            var storedDataJson = HttpContext.Session.GetString(request.Email);
            if (storedDataJson == null)
                return BadRequest("Session expired. Please register again.");

            var requestData = JsonConvert.DeserializeObject<RegisterRequest>(storedDataJson);

            var client = new ClientDetails
            {
                FirstName = requestData.FirstName,
                LastName = requestData.LastName,
                Email = requestData.Email,
                Username = requestData.Username,
                PasswordHash = PasswordHasher.HashPassword(requestData.Password),
                CompanyName = requestData.CompanyName,
                JobTitle = requestData.JobTitle,
                CreatedAt = DateTime.Now
            };

            _context.ClientDetails.Add(client);
            _context.SaveChanges();

            return Ok("Registration complete.");
        }

        [HttpPost("send-otp")]
        public async Task<IActionResult> SendOtp([FromQuery] string email)
        {
            if (string.IsNullOrWhiteSpace(email))
                return BadRequest(new { message = "Email is required." });

            var response = await _resetPassword.SendOtpAsync(email);

            if (!response.Success)
                return BadRequest(new { message = response.Message });

            return Ok(new { message = response.Message });
        }

        [HttpPost("verify-otp-and-reset-password")]
        public async Task<IActionResult> VerifyOtpAndResetPassword([FromQuery] string Email, [FromQuery] string Otp, [FromQuery] string NewPassword)
        {
            if (string.IsNullOrWhiteSpace(Email) ||
                string.IsNullOrWhiteSpace(Otp) ||
                string.IsNullOrWhiteSpace(NewPassword))
            {
                return BadRequest(new { message = "Email, OTP, and new password are required." });
            }

            var success = await _resetPassword.VerifyOtpAndResetPasswordAsync(Email, Otp, NewPassword);

            if (!success)
                return BadRequest(new { message = "OTP invalid or expired, or email not found." });

            return Ok(new { message = "Password reset successful." });
        }

    }
}
