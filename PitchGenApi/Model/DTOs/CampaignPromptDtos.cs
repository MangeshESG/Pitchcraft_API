namespace PitchGenApi.Model.DTOs
{
    public class StartCampaignDto
    {
        public string? UserId { get; set; }
        public string? SystemPrompt { get; set; }
    }

    public class CampaignChatDto
    {
        public string? UserId { get; set; }
        public string? Message { get; set; }
    }

    public class FeedbackDto
    {
        public string? UserId { get; set; }
        public string? Feedback { get; set; }
    }

    public class ApproveCampaignDto
    {
        public string? UserId { get; set; }
    }

    // ✅ DTO for generating sample email: matches your real Contact model
    public class SampleEmailDto
    {
        public string? UserId { get; set; }
        public string? InstructionMessage { get; set; }  // <- new property, frontend will send this

        public string? FullName { get; set; }      // maps to Contact.full_name
        public string? CompanyName { get; set; }   // maps to Contact.company_name
        public string? Email { get; set; }         // optional, maps to Contact.email
        public string? JobTitle { get; set; }      // optional, maps to Contact.job_title
        public string? Country { get; set; }       // optional, maps to Contact.country_or_address
    }
}