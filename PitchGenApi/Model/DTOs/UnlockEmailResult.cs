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

        /// <summary>
        /// The employer the search turned up along the way, for the extension to
        /// fill into the contact form. Unlike <see cref="Diagnostics"/> this goes
        /// to every caller: it is contact data, not a trace.
        ///
        /// Null when no search ran - a cache hit or a Prospeo hit answers from a
        /// stored address and never researches the company.
        /// </summary>
        public UnlockCompanyDetails? Company { get; set; }

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

    /// <summary>
    /// The employer behind the address, as reported by the email search. A
    /// personal LinkedIn profile shows none of this, so the search is usually
    /// the only place the extension can get it. Each field is null when it could
    /// not be sourced.
    /// </summary>
    public sealed class UnlockCompanyDetails
    {
        /// <summary>The company's own homepage, never its LinkedIn page.</summary>
        public string? Website { get; set; }

        public string? Industry { get; set; }

        /// <summary>A headcount band, e.g. "11-50".</summary>
        public string? Size { get; set; }

        /// <summary>True when at least one of the three was found.</summary>
        public bool HasAny =>
            !string.IsNullOrWhiteSpace(Website) ||
            !string.IsNullOrWhiteSpace(Industry) ||
            !string.IsNullOrWhiteSpace(Size);
    }
}
