using PitchGenApi.Repository;

namespace PitchGenApi.Interfaces
{
    public interface IResetPassworde
    {
        Task<bool> VerifyOtpAndResetPasswordAsync(string email, string otp, string newPassword);
    }
}
