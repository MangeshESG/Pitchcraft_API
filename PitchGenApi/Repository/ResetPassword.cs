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
}
