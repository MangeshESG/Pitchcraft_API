namespace PitchGenApi.Model.DTOs
{
    public class StartCampaignDto
    {
        public string? UserId { get; set; }
        public string? SystemPrompt { get; set; }
        public string? Model { get; set; } // Add this
    }

    public class CampaignChatDto
    {
        public string? UserId { get; set; }
        public string? Message { get; set; }
        public string? Model { get; set; } // Add this
    }





    // ✅ DTO for generating sample email: matches your real Contact model

}