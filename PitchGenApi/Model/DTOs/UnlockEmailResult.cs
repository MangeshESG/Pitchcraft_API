namespace PitchGenApi.Model.DTOs
{
    public class UnlockEmailResult
    {
        public string? ContactID { get; init; }
        public bool Success { get; init; }
        public string Email { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public string Source { get; init; } = string.Empty;

        /// <summary>
        /// A trace of the unlock, attached only when the caller is an admin.
        /// Null for everyone else - it carries the raw prompt and raw model
        /// output, so it must never reach a normal client.
        /// </summary>
        public UnlockDiagnostics? Diagnostics { get; set; }

        /// <summary>
        /// True when this answer came out of the 30-day cache, so the extension
        /// should offer the "look it up again from other sources" checkbox. False
        /// once Prospeo or the AI fallback has actually been asked.
        /// </summary>
        public bool CanRetryFromOtherSources { get; set; }

        /// <summary>
        /// The address the cache held before this unlock. Set only on a forced
        /// refresh, so the extension can show what the fresh lookup replaced.
        /// </summary>
        public string? PreviousEmail { get; set; }

        public static UnlockEmailResult Succeeded(
            string? contactId,
            string email,
            string status,
            string source = "prospeo") =>
            new()
            {
                ContactID = contactId,
                Success = true,
                Email = email,
                Status = status,
                Source = source
            };

        public static UnlockEmailResult Failed(string? contactId, string status) =>
            new() { ContactID = contactId, Success = false, Status = status };
    }
}
