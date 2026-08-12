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
