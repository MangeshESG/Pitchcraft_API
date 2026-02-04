using PitchGenApi.Interfaces;
using PitchGenApi.Model;
using Microsoft.Extensions.Configuration;
using PitchGenApi.Helpers;

namespace PitchGenApi.Services
{
    public class CompanyAlertService : ICompanyAlertService
    {
        private readonly IRegisterEmailSender _emailSender;
        private readonly string _companyEmail;

        public CompanyAlertService(
            IRegisterEmailSender emailSender,
            IConfiguration configuration)
        {
            _emailSender = emailSender;
            _companyEmail = configuration["CompanyAlerts:Email"]!;
        }

        public async Task SendUserRegisteredAlert(ClientDetails user, string ip, string browser)
        {
            var subject = "🆕 New User Registration";

            var body = $@"
                <h2>New Registration</h2>
                <p><b>Name:</b> {user.FirstName} {user.LastName}</p>
                <p><b>Email:</b> {user.Email}</p>
                <p><b>Username:</b> {user.Username}</p>
                <p><b>Company:</b> {user.CompanyName}</p>
                <p><b>IP:</b> {ip}</p>
                <p><b>Browser:</b> {browser}</p>
                <p><b>Time:</b> {DateTime.UtcNow}</p>
            ";

            // 👇 reuse EXISTING infrastructure
            await _emailSender.SendResetPasswordEmailAsync(
                _companyEmail,
                "N/A",
                subject,
                ip,
                browser
            );
        }

        public async Task SendUserLoginAlert(ClientDetails user, string ip, string browser)
        {
            var subject = "🔐 User Login Detected";

            var body = $@"
                <h2>User Login</h2>
                <p><b>Name:</b> {user.FirstName} {user.LastName}</p>
                <p><b>Email:</b> {user.Email}</p>
                <p><b>Username:</b> {user.Username}</p>
                <p><b>IP:</b> {ip}</p>
                <p><b>Browser:</b> {browser}</p>
                <p><b>Time:</b> {DateTime.UtcNow}</p>
            ";

            await _emailSender.SendResetPasswordEmailAsync(
                _companyEmail,
                "N/A",
                subject,
                ip,
                browser
            );
        }
    }
}
