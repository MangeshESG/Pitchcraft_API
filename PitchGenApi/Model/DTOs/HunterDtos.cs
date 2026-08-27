using System.Text.Json.Serialization;

namespace PitchGenApi.Model.DTOs
{
    /// <summary>
    /// What the unlock flow knows about the person when it escalates to
    /// Hunter. Hunter needs a name plus something that identifies the employer,
    /// so the caller passes everything it has and the service works out which
    /// combination Hunter can actually be asked with.
    /// </summary>
    public sealed class HunterLookupRequest
    {
        public string? FullName { get; set; }

        /// <summary>
        /// The company website the AI search reported. Tried first: it is the
        /// only domain here that came out of researching this person, rather
        /// than out of whatever the profile page happened to show. What the
        /// extension scrapes is often the company's LinkedIn page, and asking
        /// Hunter about linkedin.com finds nobody.
        /// </summary>
        public string? AiWebsite { get; set; }

        /// <summary>A bare domain, when the caller already has one.</summary>
        public string? Domain { get; set; }

        /// <summary>A company website, which a domain is parsed out of.</summary>
        public string? CompanyUrl { get; set; }

        /// <summary>Company name, used only when no domain can be worked out.</summary>
        public string? Company { get; set; }

        /// <summary>
        /// An address an earlier stage produced. Its domain is the last resort
        /// when nothing else names the employer: a low-confidence AI guess is
        /// still usually right about which company the person works for.
        /// </summary>
        public string? EmailHint { get; set; }
    }

    /// <summary>
    /// One Hunter lookup, in the shape the unlock flow and the admin trace both
    /// read. Carries the raw exchange so a failed lookup can be diagnosed
    /// without re-running it.
    /// </summary>
    public sealed class HunterLookupResult
    {
        public bool ApiKeyConfigured { get; set; }

        public string Endpoint { get; set; } = "";

        /// <summary>The request URL with the API key removed.</summary>
        public string RequestUrl { get; set; } = "";

        public int? HttpStatus { get; set; }

        /// <summary>The verbatim Hunter response body.</summary>
        public string? RawResponse { get; set; }

        public string? Email { get; set; }

        /// <summary>Hunter's 0-100 score, read the same way as AI confidence.</summary>
        public int Score { get; set; }

        /// <summary>"valid", "accept_all", "unknown" - Hunter's own verification.</summary>
        public string? VerificationStatus { get; set; }

        /// <summary>The domain Hunter was actually asked about.</summary>
        public string? Domain { get; set; }

        /// <summary>
        /// Which input that domain came from. Worth recording: a lookup that
        /// searched the wrong company looks identical to one that searched the
        /// right company and found nobody.
        /// </summary>
        public string? DomainSource { get; set; }

        public string? Position { get; set; }

        /// <summary>How many public sources Hunter says back the address.</summary>
        public int SourceCount { get; set; }

        /// <summary>Why this lookup produced nothing usable, when it did not.</summary>
        public string? RejectedBecause { get; set; }

        public int ElapsedMs { get; set; }

        /// <summary>True only when a usable address came back.</summary>
        public bool Found => !string.IsNullOrWhiteSpace(Email);

        public static HunterLookupResult Skipped(string reason) =>
            new() { RejectedBecause = reason };
    }

    // ------------------------------------------------- Hunter API wire format

    public sealed class HunterEmailFinderResponseDto
    {
        [JsonPropertyName("data")]
        public HunterEmailFinderDataDto? Data { get; set; }

        [JsonPropertyName("errors")]
        public List<HunterApiErrorDto>? Errors { get; set; }
    }

    public sealed class HunterEmailFinderDataDto
    {
        [JsonPropertyName("email")]
        public string? Email { get; set; }

        [JsonPropertyName("score")]
        public int? Score { get; set; }

        [JsonPropertyName("domain")]
        public string? Domain { get; set; }

        [JsonPropertyName("position")]
        public string? Position { get; set; }

        [JsonPropertyName("company")]
        public string? Company { get; set; }

        [JsonPropertyName("verification")]
        public HunterVerificationDto? Verification { get; set; }

        [JsonPropertyName("sources")]
        public List<HunterSourceDto>? Sources { get; set; }
    }

    public sealed class HunterVerificationDto
    {
        [JsonPropertyName("date")]
        public string? Date { get; set; }

        [JsonPropertyName("status")]
        public string? Status { get; set; }
    }

    public sealed class HunterSourceDto
    {
        [JsonPropertyName("domain")]
        public string? Domain { get; set; }

        [JsonPropertyName("uri")]
        public string? Uri { get; set; }
    }

    public sealed class HunterApiErrorDto
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("code")]
        public int Code { get; set; }

        [JsonPropertyName("details")]
        public string? Details { get; set; }
    }
}
