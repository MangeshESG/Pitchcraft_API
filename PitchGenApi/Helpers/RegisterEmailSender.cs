using System.Net;
using System.Net.Mail;

namespace PitchGenApi.Helpers
{
    public class RegisterEmailSender
    {
        private const string FromEmail = "aamirskdev24@gmail.com";
        private const string FromName = "PitchGen";
        private const string FromPassword = "kjhm hbch mtgu zond"; // Consider using secrets manager/env vars

        private static SmtpClient CreateSmtpClient()
        {
            return new SmtpClient
            {
                Host = "smtp.gmail.com",
                Port = 587,
                EnableSsl = true,
                Credentials = new NetworkCredential(FromEmail, FromPassword)
            };
        }

        public static void SendOtpEmail(string toEmail, string otp, string firstName, string ipAddress, string browserName)
        {
            var fromAddress = new MailAddress(FromEmail, FromName);
            var toAddress = new MailAddress(toEmail);
            const string subject = "Your Two-Factor Authentication Code - PitchKraft.ai";

            string body = $@"
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
                  font-size: 24px;
                  font-weight: bold;
                  color: #2f54eb;
                  letter-spacing: 5px;
                  background-color: #f0f2ff;
                  padding: 10px 20px;
                  display: inline-block;
                  border-radius: 5px;
                  margin: 20px 0;
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
                <p>Please find below your two-factor authentication code for <strong>PitchKraft.ai</strong>.</p>
                <div class='otp-code'>{otp}</div>
                <p>This code is valid only for <strong>5 minutes</strong> from the time it was generated.</p>

                <h4>Login Attempt Details:</h4>
                <ul>
                  <li><strong>Username:</strong> {toEmail}</li>
                  <li><strong>IP Address:</strong> {ipAddress}</li>
                  <li><strong>Browser:</strong> {browserName}</li>
                </ul>

                <p>If you think this wasn't you, please <a href='https://www.pitchkraft.ai'>contact us</a> or email <a href='mailto:support@pitchkraft.ai'>support@pitchkraft.ai</a>.</p>

                <div class='footer'>
                  Regards,<br/>
                  <strong>Support Team</strong><br/>
                  PitchKraft<br/>
                  <a href='mailto:support@pitchkraft.ai'>support@pitchkraft.ai</a><br/>
                  <a href='https://www.pitchkraft.ai'>www.pitchkraft.ai</a>
                </div>
              </div>
            </body>
            </html>";

            using var message = new MailMessage(fromAddress, toAddress)
            {
                Subject = subject,
                Body = body,
                IsBodyHtml = true
            };

            using var smtp = CreateSmtpClient();
            smtp.Send(message);
        }

        public static async Task SendEmailAsync(string toEmail, string subject, string body)
        {
            var fromAddress = new MailAddress(FromEmail, FromName);
            var toAddress = new MailAddress(toEmail);

            using var message = new MailMessage(fromAddress, toAddress)
            {
                Subject = subject,
                Body = body,
                IsBodyHtml = false
            };

            using var smtp = CreateSmtpClient();
            await smtp.SendMailAsync(message);
        }

        public static async Task TrustOtpEmail(string toEmail, string otp)
        {
            var fromAddress = new MailAddress(FromEmail, FromName);
            var toAddress = new MailAddress(toEmail);
            using var message = new MailMessage(fromAddress, toAddress)
            {
                Subject = "login otp trust device" ,
                Body = $"this is your otp {otp} for login" ,
                IsBodyHtml = false
            };

            using var smtp = CreateSmtpClient();
            await smtp.SendMailAsync(message);
        }
    }

    public static class OtpGenerator
    {
        public static string GenerateSecureOtp(int length = 6)
        {
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
            var random = new Random();
            return new string(Enumerable.Repeat(chars, length)
                .Select(s => s[random.Next(s.Length)]).ToArray());
        }
    }
}
