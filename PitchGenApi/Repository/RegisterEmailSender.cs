using Microsoft.AspNetCore.Components.Routing;
using Microsoft.EntityFrameworkCore.Metadata.Internal;
using Org.BouncyCastle.Utilities.Net;
using PitchGenApi.Models;
using Stripe.V2;
using Stripe;
using System;
using System.Net;
using System.Net.Mail;
using System.Net.Security;
using System.Reflection.Metadata;
using System.Security.Cryptography.X509Certificates;
using static System.Runtime.InteropServices.JavaScript.JSType;
using System.Threading.Tasks;
using PitchGenApi.Database;
using Microsoft.EntityFrameworkCore;
using static System.Net.WebRequestMethods;

namespace PitchGenApi.Helpers
{
    public class RegisterEmailSender : IRegisterEmailSender
    {
        private readonly AppDbContext _context;
        private readonly EmailTemplateHelper _templateHelper;

        private const string FromEmail = "support@pitchkraft.ai";
        private const string FromName = "PitchGen";
        private const string FromPassword = "Mdx020*0m";

        public RegisterEmailSender(AppDbContext context, EmailTemplateHelper templateHelper)
        {
            _context = context;
            _templateHelper = templateHelper;
        }
        static RegisterEmailSender()
        {
            ServicePointManager.ServerCertificateValidationCallback =
                (sender, certificate, chain, sslPolicyErrors) => true;
        }

        private static SmtpClient CreateSmtpClient() => new()
        {
            Host = "mail.pitchkraft.ai",
            Port = 587,
            EnableSsl = true,
            Credentials = new NetworkCredential(FromEmail, FromPassword)
        };

        // Generic Send
        private async Task SendAsync(string to, string subject, string body)
        {
            using var msg = new MailMessage(new MailAddress(FromEmail, FromName), new MailAddress(to))
            {
                Subject = subject,
                Body = body,
                IsBodyHtml = true
            };
            using var smtp = CreateSmtpClient();
            await smtp.SendMailAsync(msg);
        }

        // 1️⃣ OTP Email
        public async Task SendOtpEmail(string to, string otp, string firstName, string ip, string browser)
        {

            // Fetch the email template by name (TemplateName column in DB)
            var template = await _context.EmailTemplates
                .FirstOrDefaultAsync(x => x.TemplateName == "RegisterOtpEmail");

            if (template == null)
                throw new Exception("OtpEmail template not found");

            // Prepare dynamic data for placeholders
            var data = new Dictionary<string, string>
            {
                { "FirstName", firstName },
                { "OTP", otp },
                { "IPAddress", ip },
                { "BrowserName", browser },
                { "UserEmail", to }
            };

            // Replace placeholders in subject and body
            string subject = _templateHelper.ReplacePlaceholders(template.Subject, data);
            string body = _templateHelper.ReplacePlaceholders(template.Body, data);

            // Send the email
            await SendAsync(to, subject, body);
        }



        public  async Task SendResetPasswordEmailAsync(string to, string otp, string firstName, string ip, string browser)
        {
            // Fetch the email template by name (TemplateName column in DB)
            var template = await _context.EmailTemplates
                .FirstOrDefaultAsync(x => x.TemplateName == "ResetOtp");

            if (template == null)
                throw new Exception("OtpEmail template not found");

            // Prepare dynamic data for placeholders
            var data = new Dictionary<string, string>
            {
                { "FirstName", firstName },
                { "OTP", otp },
                { "IPAddress", ip },
                { "BrowserName", browser },
                { "UserEmail", to }
            };

            // Replace placeholders in subject and body
            string subject = _templateHelper.ReplacePlaceholders(template.Subject, data);
            string body = _templateHelper.ReplacePlaceholders(template.Body, data);

            // Send the email
            await SendAsync(to, subject, body);
        }

        public async Task TrustOtpEmail(string to, string otp, string firstName, string ip, string browser)
        {
            var template = await _context.EmailTemplates
               .FirstOrDefaultAsync(x => x.TemplateName == "TrustOtp");

            if (template == null)
                throw new Exception("OtpEmail template not found");

            // Prepare dynamic data for placeholders
            var data = new Dictionary<string, string>
            {
                { "FirstName", firstName },
                { "OTP", otp },
                { "IPAddress", ip },
                { "BrowserName", browser },
                { "UserEmail", to }
            };

            // Replace placeholders in subject and body
            string subject = _templateHelper.ReplacePlaceholders(template.Subject, data);
            string body = _templateHelper.ReplacePlaceholders(template.Body, data);

            // Send the email
            await SendAsync(to, subject, body);
        }

        public async Task DomainVerifyOTP(string Email, string otp, string firstName, string ip, string browsername,string username)
        {
            var template = await _context.EmailTemplates
               .FirstOrDefaultAsync(x => x.TemplateName == "DomainVerification");

            string domain = Email.Split('@')[1].Trim().ToLower();

            if (template == null)
                throw new Exception("OtpEmail template not found");

            // Prepare dynamic data for placeholders
            var data = new Dictionary<string, string>
            {
                { "FirstName", firstName },
                { "domainname", domain },
                { "OTP", otp },
                { "UserEmail", username },
                { "IPAddress", ip },
                { "BrowserName", browsername },
            };

            // Replace placeholders in subject and body
            string subject = _templateHelper.ReplacePlaceholders(template.Subject, data);
            string body = _templateHelper.ReplacePlaceholders(template.Body, data);

            // Send the email
            await SendAsync(Email, subject, body);
        }

        public async Task SendInvoiceEmailAsync(string toEmail, string customerName, string invoiceNumber, string invoiceDate, string amount, string invoicePdfUrl, string senderName, string supportEmail)
        {
            var fromAddress = new MailAddress(FromEmail, FromName);
            var toAddress = new MailAddress(toEmail);

            string body = $@"
                <!doctype html>
                <html lang='en'>
                <head>
                  <meta charset='utf-8'>
                  <meta name='viewport' content='width=device-width,initial-scale=1'>
                  <title>Invoice Paid</title>
                </head>
                <body style='margin:0;padding:0;background-color:#f4f6f8;font-family:Arial,Helvetica,sans-serif;'>
                  <table width='100%' cellpadding='0' cellspacing='0' role='presentation'>
                    <tr>
                      <td align='center' style='padding:20px 10px;'>
                        <table width='600' cellpadding='0' cellspacing='0' role='presentation' style='background:#ffffff;border-radius:8px;overflow:hidden;box-shadow:0 4px 16px rgba(0,0,0,0.06);'>
          
                          <!-- Header -->
                          <tr>
                            <td style='padding:18px 24px;background:linear-gradient(90deg,#0f172a,#0b1220);color:#ffffff;'>
                              <h1 style='margin:0;font-size:20px;font-weight:600;'>Payment Receipt</h1>
                            </td>
                          </tr>

                          <!-- Body -->
                          <tr>
                            <td style='padding:24px;'>
                              <p style='margin:0 0 12px 0;color:#111827;font-size:15px;'>
                                Dear <strong>{customerName}</strong>,
                              </p>

                              <p style='margin:0 0 18px 0;color:#374151;font-size:14px;line-height:1.5;'>
                                Thank you for your business.<br>
                                Payment for the invoice <strong>{invoiceNumber}</strong> has been successful and the invoice is available for download using the link below.
                              </p>

                              <!-- Invoice summary box -->
                              <table cellpadding='0' cellspacing='0' role='presentation' style='width:100%;border:1px solid #e6e9ee;border-radius:6px;padding:12px;background:#fbfcfe;'>
                                <tr>
                                  <td style='padding:6px 8px;font-size:13px;color:#374151;vertical-align:top;'>
                                    <strong>Invoice Date</strong><br>
                                    <span style='color:#6b7280;'>{invoiceDate}</span>
                                  </td>
                                  <td style='padding:6px 8px;font-size:13px;color:#374151;vertical-align:top;'>
                                    <strong>Amount</strong><br>
                                    <span style='color:#6b7280;'>{amount}</span>
                                  </td>
                                  <td style='padding:6px 8px;font-size:13px;color:#374151;vertical-align:top;'>
                                    <strong>Invoice No.</strong><br>
                                    <span style='color:#6b7280;'>{invoiceNumber}</span>
                                  </td>
                                </tr>
                              </table>

                              <div style='height:18px;'></div>

                              <!-- Button -->
                              <table cellpadding='0' cellspacing='0' role='presentation'>
                                <tr>
                                  <td align='left'>
                                    <a href='{invoicePdfUrl}' style='display:inline-block;padding:12px 20px;background:#0066ff;color:#ffffff;text-decoration:none;border-radius:6px;font-weight:600;font-size:14px;'>
                                      Download Invoice (PDF)
                                    </a>
                                  </td>
                                </tr>
                              </table>

                              <div style='height:18px;'></div>

                              <p style='margin:0;color:#374151;font-size:14px;line-height:1.5;'>
                                It was great working with you, looking forward to doing business again.
                              </p>

                              <div style='height:18px;'></div>

                              <p style='margin:0;color:#6b7280;font-size:13px;'>
                                Regards,<br>
                                <strong>{senderName}</strong>
                              </p>
                            </td>
                          </tr>

                          <!-- Footer -->
                          <tr>
                            <td style='padding:12px 24px;background:#f9fafb;color:#9ca3af;font-size:12px;text-align:center;'>
                              <span>
                                If you have any questions, reply to this email or contact support at 
                                <a href='mailto:{supportEmail}' style='color:#6b7280;text-decoration:underline;'>{supportEmail}</a>
                              </span>
                            </td>
                          </tr>
                        </table>

                        <div style='max-width:600px;margin-top:12px;color:#9ca3af;font-size:12px;'>
                          This is an automated message. Please do not reply if this is not applicable.
                        </div>
                      </td>
                    </tr>
                  </table>
                </body>
                </html>";

            using var message = new MailMessage(fromAddress, toAddress)
            {
                Subject = $"Invoice Payment Confirmation - {invoiceNumber}",
                Body = body,
                IsBodyHtml = true
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
