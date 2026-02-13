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
using PitchGenApi.Model;

namespace PitchGenApi.Helpers
{
    public class RegisterEmailSender : IRegisterEmailSender
    {
        private readonly AppDbContext _context;
        private readonly EmailTemplateHelper _templateHelper;
        private readonly string _companyEmail;

        private const string FromEmail = "support@pitchkraft.ai";
        private const string FromName = "PitchGen";
        private const string FromPassword = "Mdx020*0m";

        public RegisterEmailSender(AppDbContext context, EmailTemplateHelper templateHelper, IConfiguration configuration)
        {
            _context = context;
            _templateHelper = templateHelper;
            _companyEmail = configuration["CompanyAlerts:Email"]!;
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

        public async Task RegistrationDetect(ClientDetails user, string ip, string browser)
        {
            var template = await _context.EmailTemplates
               .FirstOrDefaultAsync(x => x.TemplateName == "RegistrationDetect");

            if (template == null)
                throw new Exception("RegistrationDetect template not found");

            var date = DateTime.Now;

            // Prepare dynamic data for placeholders
            var data = new Dictionary<string, string>
            {
                { "FirstName", user.FirstName },
                { "LastName", user.LastName },
                { "Email", user.Email },
                { "Username", user.Username },
                { "CompanyName", user.CompanyName },
                { "IP", ip },
                { "Browser", browser },
                { "DateTime", date.ToString("dd MMM yyyy, hh:mm tt") },
            };

            // Replace placeholders in subject and body
            string subject = _templateHelper.ReplacePlaceholders(template.Subject, data);
            string body = _templateHelper.ReplacePlaceholders(template.Body, data);

            // Send the email
            await SendAsync(_companyEmail, subject, body);
        }
        
        public async Task LoginDetect(ClientDetails user, string ip, string browser)
        {
            var template = await _context.EmailTemplates
               .FirstOrDefaultAsync(x => x.TemplateName == "LoginDetect");

            if (template == null)
                throw new Exception("LoginDetect template not found");

            var date = DateTime.Now;

            // Prepare dynamic data for placeholders
            var data = new Dictionary<string, string>
            {
                { "FirstName", user.FirstName },
                { "LastName", user.LastName },
                { "Email", user.Email },
                { "Username", user.Username },
                { "IP", ip },
                { "Browser", browser },
                { "DateTime", date.ToString("dd MMM yyyy, hh:mm tt") },
            };

            // Replace placeholders in subject and body
            string subject = _templateHelper.ReplacePlaceholders(template.Subject, data);
            string body = _templateHelper.ReplacePlaceholders(template.Body, data);

            // Send the email
            await SendAsync(_companyEmail, subject, body);
        }
        
        public async Task StopGenrationMail(EmailRequest req, string ip, string browser)
        {
            var template = await _context.EmailTemplates
               .FirstOrDefaultAsync(x => x.TemplateName == "StopGenrationMail");

            if (template == null)
                throw new Exception("LoginDetect template not found");
            var user = await _context.ClientDetails.FirstOrDefaultAsync(x => x.Id == req.userid);
            var date = DateTime.Now;
            var reportType = req.IsPauseReport ? "Pause Report" : "Processing Report";
            var status = req.IsPauseReport ? "Process was paused by the user" : "Process was completed by the user";

            // Prepare dynamic data for placeholders
            var data = new Dictionary<string, string>
            {
                { "ReportType", reportType },
                { "Status", status },
                { "Username", user.Email },
                { "UserId", req.userid.ToString()},
                { "UserRole", req.Role },
                { "FirstName", user.FirstName },
                { "LastName", user.LastName  },
                { "IPAddress", ip},
                { "Browser", browser},
                { "StartTime", req.StartTime?.ToString("dd MMM yyyy, hh:mm tt") ?? "N/A" },
                { "EndTime", req.EndTime?.ToString("dd MMM yyyy, hh:mm tt") ?? "N/A"},
                { "TimeSpent", req.TimeSpent},
                { "SuccessReq", req.SuccessReq.ToString() ?? "0"},
                { "TotalTokensUsed", req.TotalTokensUsed.ToString()},
                { "TotalCost", req.Cost.ToString()},
                { "PromptText", req.PromptText ?? "N/A"},
                { "LastPitch", req.lastPitch ?? "N/A"},
                { "GeneratedPitchesCount", req.GeneratedPitches?.Count.ToString() ?? "0" },
            };

            // Replace placeholders in subject and body
            string subject = _templateHelper.ReplacePlaceholders(template.Subject, data);
            string body = _templateHelper.ReplacePlaceholders(template.Body, data);

            // Send the email
            await SendAsync(_companyEmail, subject, body);
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
