using PitchGenApi.Repository;

namespace PitchGenApi.Interfaces
{
    public interface IResetPassworde
    {
        Task<OtpResponse> SendOtpAsync(string email);
        Task<bool> VerifyOtpAndResetPasswordAsync(string email, string otp, string newPassword);
    }
}
