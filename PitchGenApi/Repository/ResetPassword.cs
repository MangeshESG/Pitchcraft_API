using Microsoft.EntityFrameworkCore;
using PitchGenApi.Database;
using PitchGenApi.Helpers;
using PitchGenApi.Interfaces;
using PitchGenApi.Model;  // Your existing namespace for EmailOtpVerification

namespace PitchGenApi.Repository
{
    public class ResetPassword :  IResetPassworde
    {
        private readonly AppDbContext _context;

        public ResetPassword(AppDbContext context)
        {
            _context = context;
        }

        public async Task<OtpResponse> SendOtpAsync(string email)
        {
            var customerExists = await _context.ClientDetails
                .AnyAsync(c => c.Email == email);

            if (!customerExists)
            {
                return new OtpResponse
                {
                    Success = false,
                    Message = "Invalid email ID."
                };
            }

            string otp = OtpGenerator.GenerateSecureOtp();  // default 6-character
            DateTime expiry = DateTime.UtcNow.AddMinutes(10);

            var otpEntry = new EmailOtpVerification
            {
                Email = email,
                OTP = otp,
                IsVerified = false,
                OtpType ="reset password",
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = expiry
            };

            _context.EmailOtpVerifications.Add(otpEntry);
            await _context.SaveChangesAsync();

            var subject = "Your OTP Code";
            var body = $"Your OTP code is: {otp}. It will expire in 10 minutes.";

            await RegisterEmailSender.SendEmailAsync(email, subject, body);

            return new OtpResponse
            {
                Success = true,
                Message = "OTP sent successfully."
            };
        }

        public async Task<bool> VerifyOtpAndResetPasswordAsync(string email, string otp, string newPassword)
        {
            var otpEntry = await _context.EmailOtpVerifications
                .Where(x => x.Email == email && x.OTP == otp && !x.IsVerified && x.ExpiresAt > DateTime.UtcNow)
                .OrderByDescending(x => x.CreatedAt)
                .FirstOrDefaultAsync();

            if (otpEntry == null)
                return false;

            var user = await _context.ClientDetails.FirstOrDefaultAsync(x => x.Email == email);
            if (user == null)
                return false;

            // Mark OTP as verified
            otpEntry.IsVerified = true;

            // ⚠️ In real apps: Hash the password before storing
            user.PasswordHash = PasswordHasher.HashPassword(newPassword);
            await _context.SaveChangesAsync();
            return true;
        }


    }
    public class OtpResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; }
    }

}
