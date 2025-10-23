using PitchGenApi.Repository;

namespace PitchGenApi.Interfaces
{
    public interface IResetPassworde
    {
        Task<OtpResponse> SendOtpAsync(string toEmail, string firstName, string ipAddress, string browserName);
        Task<bool> VerifyOtpAndResetPasswordAsync(string email, string otp, string newPassword);
    }
}
