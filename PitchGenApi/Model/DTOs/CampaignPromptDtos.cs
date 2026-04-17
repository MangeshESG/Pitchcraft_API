namespace PitchGenApi.Model.DTOs
{
    public class ChatRequestDto
    {
        public string UserId { get; set; }
        public string Message { get; set; }
        public string SystemPrompt { get; set; } 
        public string Model { get; set; }
        public string? ImageUrl { get; set; }   // PNG / JPG / JPEG only
        public int? CampaignTemplateId { get; set; }

    }

    public class StartEditConversationRequest
    {
        public required string UserId { get; set; }
        public int CampaignTemplateId { get; set; }
        public required string Placeholder { get; set; }
        public required string CurrentValue { get; set; }
        public string? Model { get; set; }
        public string? ImageUrl { get; set; }   // ⭐ ADD

    }

    public class EditChatRequest
    {
        public string UserId { get; set; }
        public int CampaignTemplateId { get; set; }
        public string Message { get; set; }
        public string Model { get; set; }
        public string? ImageUrl { get; set; }   // ⭐ ADD

    }
}