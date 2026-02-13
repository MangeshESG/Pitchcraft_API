using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using PitchGenApi.Database;
using PitchGenApi.Helpers;
using PitchGenApi.Interfaces;
using PitchGenApi.Model;
using PitchGenApi.Model.DTOs;
using PitchGenApi.Repositories;
using PitchGenApi.Services;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using UglyToad.PdfPig.Graphics.Operations.PathPainting;

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
        private readonly IStripeRepository _stripe;
        private readonly IRegisterEmailSender _register;
        private readonly ICompanyAlertService _companyAlert;


        public LoginController(AppDbContext context, IStripeRepository stripe, IUserRepository userRepository, JwtService jwtService, IResetPassworde resetPassword, IRegisterEmailSender register, ICompanyAlertService companyAlert)
        {
            _context = context;
            _userRepository = userRepository;
            _jwtService = jwtService;
            _resetPassword = resetPassword;
            _stripe = stripe;
            _register = register;
            _companyAlert = companyAlert;
        }

        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginRequestDTO dto)
        {
            var user = await _userRepository.GetUser(dto.username);

            var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
            var userAgent = Request.Headers["User-Agent"].ToString();
            var browserName = EmailTrackingHelper.GetBrowserName(userAgent);

            if (user == null || !VerifyPassword(dto.password, user.PasswordHash))
            {
                return Unauthorized(new { Message = "Invalid credentials" });
            }

            // ✅ Agar trustednumber match karta hai to direct token return karo
            if (user.TrustDiviceNumber != null && user.TrustDiviceNumber == dto.trustednumber && user.TrustExpiry > DateTime.Now)
            {
                var tokenDirect = _jwtService.GeneratenewToken(dto.username, user.Id, user.FirstName.ToString(), user.LastName.ToString());

                try
                {
                    await _register.LoginDetect(user, ipAddress, browserName);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Login alert email failed: " + ex.Message);
                }
                return Ok(new


                {
                    Token = tokenDirect,
                    user.IsAdmin
                });
            }

            // ✅ Yaha se OTP flow chalega
            string otp = OtpGenerator.GenerateSecureOtp();

            if (user.TrustDiviceNumber == null || user.TrustDiviceNumber != dto.trustednumber)
            {
                await _register.TrustOtpEmail(user.Email, otp, user.FirstName, ipAddress, browserName);
            }

            var otpEntity = new EmailOtpVerification
            {
                Email = user.Email,
                OTP = otp,
                username = dto.username,
                IsVerified = false,
                OtpType = "login verify",
                CreatedAt = DateTime.Now,
                ExpiresAt = DateTime.Now.AddMinutes(10)
            };

            _context.EmailOtpVerifications.Add(otpEntity);
            _context.SaveChanges();
            return Ok(new
            {
                success = true,
                message = "OTP sent successfully"
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

        [HttpPost("verify_trust_otp")]
        public async Task<IActionResult> TrustedDivice([FromQuery] string? username, [FromQuery] string otp, [FromQuery] bool trustthisdivice)
        {
            var user = await _userRepository.GetUser(username);

            var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
            var userAgent = Request.Headers["User-Agent"].ToString();
            var browserName = EmailTrackingHelper.GetBrowserName(userAgent);

            var otpDetails = await _userRepository.GetOtpDetails(otp, username);
            
                await _register.TrustOtpEmail(user.Email, otp, user.FirstName, ipAddress, browserName);

            if (string.IsNullOrEmpty(otp) ||
                user == null ||
                otpDetails == null ||
                otpDetails.OTP != otp ||
                otpDetails.ExpiresAt < DateTime.Now ||
                otpDetails.IsVerified)
            {
                return BadRequest("Invalid OTP, try again");
            }


            otpDetails.IsVerified = true;
            _context.SaveChanges();

            if (trustthisdivice)
            {
                Random rnd = new Random();
                int r = rnd.Next(100000, 999999);
                DateTime expiry = DateTime.Now.AddDays(30);

                user.TrustDiviceNumber = r;
                user.TrustExpiry = expiry;

                await _userRepository.Update(user);

                //return Ok(new { message = "Device trusted for 30 days", code = r });
            }

            var token = _jwtService.GeneratenewToken(user.Username, user.Id, user.FirstName.ToString(), user.LastName.ToString());
            try
            {
                await _register.LoginDetect(user, ipAddress, browserName);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Login alert email failed: " + ex.Message);
            }
            return Ok(new
            {
                user.IsAdmin,
                Token = token,
                trustenumber = user.TrustDiviceNumber,
            });
        }

        // Step 1: Register Request → Generate OTP → Send Email
        [HttpPost("register")]
        public async Task<IActionResult> Register([FromBody] RegisterRequest request)
        {
            if (_context.ClientDetails.Any(u => u.Email == request.Email || u.Username == request.Username))
            {
                return BadRequest("Email or Username already exists.");
            }

            // Generate OTP
            string otp = OtpGenerator.GenerateSecureOtp();

            // Save OTP
            var otpEntity = new EmailOtpVerification
            {
                Email = request.Email,
                username = request.Username,
                OTP = otp,
                IsVerified = false,
                CreatedAt = DateTime.Now,
                OtpType = "registration",
                ExpiresAt = DateTime.Now.AddMinutes(10)
            };
            _context.EmailOtpVerifications.Add(otpEntity);

            // Save Registration Details as Temp Data (10 minutes)
            var tempRegister = new TempRegisterData
            {
                Email = request.Email,
                JsonData = JsonConvert.SerializeObject(request),
                CreatedAt = DateTime.Now,
                ExpiresAt = DateTime.Now.AddMinutes(10)
            };
            _context.TempRegisterData.Add(tempRegister);

            _context.SaveChanges();

            // Send OTP Email
            var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
            var userAgent = Request.Headers["User-Agent"].ToString();
            var browserName = EmailTrackingHelper.GetBrowserName(userAgent);

           await _register.SendOtpEmail(request.Email, otp, request.FirstName, ipAddress, browserName);

            return Ok("OTP sent to your email.");
        }

        // Step 2: Verify OTP and Save ClientDetails
        [HttpPost("registration-verify-otp")]
        public async Task<IActionResult> VerifyOtp([FromBody] VerifyOtpRequest request)
        {
            var otpRecord = _context.EmailOtpVerifications
                .FirstOrDefault(o => o.Email == request.Email && o.OTP == request.Otp);

            if (otpRecord == null)
                return BadRequest("Invalid OTP.");

            if (otpRecord.ExpiresAt < DateTime.Now)
                return BadRequest("OTP expired.");

            otpRecord.IsVerified = true;
            _context.SaveChanges();

            // 🔍 Get temporary registration data from DB
            var tempData = _context.TempRegisterData
                .FirstOrDefault(t => t.Email == request.Email /*&& t.ExpiresAt > DateTime.Now*/);

            if (tempData == null)
                return BadRequest("Registration data expired. Please register again.");

            var requestData = JsonConvert.DeserializeObject<RegisterRequest>(tempData.JsonData);

            // After deserializing tempData
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
            _context.SaveChanges(); // Save client to get generated ID

            var nextSubNumber = await _context.UserCredits.CountAsync() + 1;
            var formattedSubNumber = $"SUB-{nextSubNumber:D4}"; // e.g. SUB-0001
            var StartDate = DateTime.UtcNow;
            var EndDate = StartDate.AddMonths(1);

            await _stripe.SaveUserCreditsAsync(client.Id, "Basic", "Basic Default", formattedSubNumber, StartDate, EndDate, "Monthly",0,null);

            // Cleanup temp data
            _context.TempRegisterData.Remove(tempData);
            _context.SaveChanges(); // Save everything

            var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
            var browser = EmailTrackingHelper.GetBrowserName(
                Request.Headers["User-Agent"]
            );
            try
            {
                await _register.RegistrationDetect(client, ip, browser);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Register  alert email failed: " + ex.Message);
            }


            return Ok("Registration complete.");
        }

        [HttpPost("restpass_send-otp")]
        public async Task<IActionResult> SendOtp([FromQuery] string email)
        {
            var user = await _userRepository.GetUser(email);

            if (string.IsNullOrWhiteSpace(email))
                return BadRequest(new { message = "Email is required." });

            var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
            var userAgent = Request.Headers["User-Agent"].ToString();
            var browserName = EmailTrackingHelper.GetBrowserName(userAgent);
            string otp = OtpGenerator.GenerateSecureOtp();
            DateTime expiry = DateTime.UtcNow.AddMinutes(10);

            var otpEntry = new EmailOtpVerification
            {
                Email = email,
                OTP = otp,
                IsVerified = false,
                OtpType = "reset password",
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = expiry
            };

            _context.EmailOtpVerifications.Add(otpEntry);
            await _context.SaveChangesAsync();
            await _register.SendResetPasswordEmailAsync(email,otp, user.FirstName, ipAddress, browserName);

            return Ok(new
            {
                success = true,
                message = "OTP sent successfully"
            });
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
