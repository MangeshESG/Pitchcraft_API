using System.Text.Json.Serialization;

namespace PitchGenApi.Model.DTOs
{
    public sealed class ProspeoUnlockRequestDto
    {
        public string? Name { get; set; }
        public string? JobTitle { get; set; }
        public string? CompanyName { get; set; }
        public string? Location { get; set; }
        public string? Domain { get; set; }
        public string? CompanyUrl { get; set; }
        public string? ContactID { get; set; }
        public int ClientID { get; set; }
        public string LinkedInUrl { get; set; } = string.Empty;

        /// <summary>
        /// Set by the extension's "look it up again from other sources" checkbox.
        /// The 30-day cache sometimes holds a wrong address, so this skips the
        /// cache read and goes straight to Prospeo and then the AI fallback. The
        /// address that comes back replaces the cached one.
        /// </summary>
        [JsonPropertyName("forceRefresh")]
        public bool ForceRefresh { get; set; }
    }

    /// <summary>
    /// One Prospeo lookup, in the shape both the unlock flow and the Audience
    /// Assurance email check read. Carries the raw exchange so a failed lookup
    /// can be diagnosed from the admin trace without re-running it — and a
    /// re-run costs a Prospeo credit.
    /// </summary>
    public sealed class ProspeoLookupResult
    {
        public bool ApiKeyConfigured { get; set; }

        public string Endpoint { get; set; } = "https://api.prospeo.io/enrich-person";

        /// <summary>The request body, for the admin trace.</summary>
        public string? RequestBody { get; set; }

        public int? HttpStatus { get; set; }

        /// <summary>The verbatim Prospeo response body.</summary>
        public string? RawResponse { get; set; }

        public string? Email { get; set; }

        /// <summary>Prospeo's own status, e.g. "VERIFIED".</summary>
        public string? EmailStatus { get; set; }

        /// <summary>Whether Prospeo actually revealed the address, as opposed to knowing of one.</summary>
        public bool? Revealed { get; set; }

        /// <summary>Why this lookup produced nothing usable, when it did not.</summary>
        public string? RejectedBecause { get; set; }

        public int ElapsedMs { get; set; }

        /// <summary>True only when a verified address came back.</summary>
        public bool Found => !string.IsNullOrWhiteSpace(Email);

        public static ProspeoLookupResult Skipped(string reason) =>
            new() { RejectedBecause = reason };
    }

    public sealed class ProspeoEnrichResponseDto
    {
        [JsonPropertyName("error")]
        public bool Error { get; set; }

        [JsonPropertyName("person")]
        public ProspeoPersonDto? Person { get; set; }
    }

    public sealed class ProspeoPersonDto
    {
        [JsonPropertyName("email")]
        public ProspeoEmailDto? Email { get; set; }
    }

    public sealed class ProspeoEmailDto
    {
        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("revealed")]
        public bool Revealed { get; set; }

        [JsonPropertyName("email")]
        public string? Email { get; set; }
    }
}
