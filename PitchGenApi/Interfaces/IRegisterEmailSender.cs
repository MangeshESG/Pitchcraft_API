using PitchGenApi.Model;
using System.Threading.Tasks;

namespace PitchGenApi.Helpers
{
    public interface IRegisterEmailSender
    {
        Task SendOtpEmail(string to, string otp, string firstName, string ip, string browser);
        Task RegistrationDetect(ClientDetails user, string ip, string browser);
        Task SendResetPasswordEmailAsync(string to, string otp, string firstName, string ip, string browser);
        Task TrustOtpEmail(string to, string otp, string firstName, string ip, string browser);
        Task DomainVerifyOTP(string Email, string otp, string firstName,string ip, string browser, string username);
        Task LoginDetect(ClientDetails user, string ip, string browser);
        Task StopGenrationMail(EmailRequest req, string ip, string browser);
    }
}
