namespace PitchGenApi.Model.DTOs
{
    public class ChatRequestDto
    {
        public string UserId { get; set; }
        public string Message { get; set; }
        public string SystemPrompt { get; set; } // Optional - only needed for first message
        public string Model { get; set; } // Optional - defaults to gpt-5
    }

    public class StartEditConversationRequest
    {
        public required string UserId { get; set; }
        public int CampaignTemplateId { get; set; }
        public required string Placeholder { get; set; }
        public required string CurrentValue { get; set; }
        public string? Model { get; set; }
    }

    public class EditChatRequest
    {
        public string UserId { get; set; }
        public int CampaignTemplateId { get; set; }
        public string Message { get; set; }
        public string Model { get; set; }
    }
}