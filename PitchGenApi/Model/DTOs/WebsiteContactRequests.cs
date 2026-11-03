namespace PitchGenApi.Model.DTOs
{
    public sealed class CreateWebsiteIntegrationRequest
    {
        public int ClientId { get; set; }
        public string WebsiteName { get; set; } = "";
    }

    public sealed class RegenerateWebsiteKeyRequest
    {
        public int ClientId { get; set; }
        public int IntegrationId { get; set; }
    }

    public sealed class WebsiteContactRequest
    {
        public string FullName { get; set; } = "";
        public string FirstName { get; set; } = "";
        public string LastName { get; set; } = "";
        public string Email { get; set; } = "";
        public string? CompanyName { get; set; }
        public string? PhoneNumber { get; set; }
        public string? Message { get; set; }
    }
}
