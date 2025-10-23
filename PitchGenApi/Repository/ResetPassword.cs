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

        public async Task<OtpResponse> SendOtpAsync(string toEmail, string firstName, string ipAddress, string browserName)
        {
            var customerExists = await _context.ClientDetails
                .AnyAsync(c => c.Email == toEmail);

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
                Email = toEmail,
                OTP = otp,
                IsVerified = false,
                OtpType ="reset password",
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = expiry
            };

            _context.EmailOtpVerifications.Add(otpEntry);
            await _context.SaveChangesAsync();

            var subject = "Password Reset Verification Code – PitchKraft.ai";
            var body = $@"
                <html>
                <head>
                  <style>
                    body {{
                      font-family: Arial, sans-serif;
                      background-color: #f9f9f9;
                      color: #333;
                      padding: 20px;
                    }}
                    .container {{
                      background-color: #fff;
                      padding: 30px;
                      border-radius: 10px;
                      box-shadow: 0 0 10px rgba(0,0,0,0.1);
                    }}
                    .otp-code {{
                      font-size: 26px;
                      font-weight: bold;
                      color: #2f54eb;
                      letter-spacing: 6px;
                      background-color: #f0f2ff;
                      padding: 12px 24px;
                      display: inline-block;
                      border-radius: 6px;
                      margin: 25px 0;
                    }}
                    .footer {{
                      margin-top: 30px;
                      font-size: 13px;
                      color: #777;
                    }}
                  </style>
                </head>
                <body>
                  <div class='container'>
                    <h2>Hello {firstName},</h2>
                    <p>We received a request to reset your <strong>PitchKraft.ai</strong> account password.</p>
                    <p>Please use the verification code below to complete your password reset:</p>
                    <div class='otp-code'>{otp}</div>
                    <p>This code is valid for <strong>5 minutes</strong>. Do not share it with anyone for security reasons.</p>

                    <h4>Request Details:</h4>
                    <ul>
                      <li><strong>Email:</strong> {toEmail}</li>
                      <li><strong>IP Address:</strong> {ipAddress}</li>
                      <li><strong>Browser:</strong> {browserName}</li>
                    </ul>

                    <p>If you didn’t request a password reset, please <a href='https://www.pitchkraft.ai'>contact us</a> immediately or email 
                    <a href='mailto:support@pitchkraft.ai'>support@pitchkraft.ai</a>.</p>

                    <div class='footer'>
                      Regards,<br/>
                      <strong>Support Team</strong><br/>
                      PitchKraft<br/>
                      <a href='mailto:support@pitchkraft.ai'>support@pitchkraft.ai</a><br/>
                      <a href='https://www.pitchkraft.ai'>www.pitchkraft.ai</a>
                    </div>
                  </div>
                </body>
                </html>"
            ;

            await RegisterEmailSender.SendEmailAsync(toEmail, subject, body);

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
