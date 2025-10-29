using System.Threading.Tasks;

namespace PitchGenApi.Helpers
{
    public interface IRegisterEmailSender
    {
        Task SendOtpEmail(string to, string otp, string firstName, string ip, string browser);
        Task SendResetPasswordEmailAsync(string to, string otp, string firstName, string ip, string browser);
        Task TrustOtpEmail(string to, string otp, string firstName, string ip, string browser);
        Task SendInvoiceEmailAsync(
            string toEmail,
            string customerName,
            string invoiceNumber,
            string invoiceDate,
            string amount,
            string invoicePdfUrl,
            string senderName,
            string supportEmail);
    }
}
