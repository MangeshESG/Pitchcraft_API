namespace PitchGenApi.Interfaces
{
    /// <summary>
    /// Reads/writes the admin-controlled security switches (Settings &gt;
    /// Security). The login flow goes through here instead of hardcoding
    /// whether device-verification OTP is required.
    /// </summary>
    public interface ISecuritySettingsService
    {
        /// <summary>
        /// True when login must be confirmed with an emailed OTP. Defaults to
        /// true, so a missing row or an unreachable table keeps the stricter
        /// behaviour.
        /// </summary>
        Task<bool> IsLoginOtpEnabledAsync();

        /// <summary>Turns the login OTP requirement on or off for every user.</summary>
        Task SetLoginOtpEnabledAsync(bool enabled, string? updatedBy);
    }
}
