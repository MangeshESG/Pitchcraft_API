namespace PitchGenApi.Model
{
    /// <summary>
    /// Application-wide security switches, one row per key (see
    /// <see cref="SecuritySettingKeys"/>). Admins edit these from
    /// Settings &gt; Security and every login reads them from here.
    /// </summary>
    public class SecuritySetting
    {
        public int id { get; set; }

        /// <summary>One of <see cref="SecuritySettingKeys"/>.</summary>
        public string setting_key { get; set; } = "";

        /// <summary>Stored as text so future switches aren't limited to booleans.</summary>
        public string setting_value { get; set; } = "";

        public DateTime updated_at { get; set; }

        public string? updated_by { get; set; }
    }

    public static class SecuritySettingKeys
    {
        /// <summary>
        /// "true" (default) sends a device-verification OTP on every login from
        /// an untrusted device. "false" signs the user in on username and
        /// password alone.
        /// </summary>
        public const string LoginOtpEnabled = "login_otp_enabled";

        public static readonly string[] All = { LoginOtpEnabled };

        public static bool IsKnown(string? key) =>
            !string.IsNullOrWhiteSpace(key) &&
            All.Contains(key, StringComparer.OrdinalIgnoreCase);
    }
}
