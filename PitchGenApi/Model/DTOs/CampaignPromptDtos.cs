namespace PitchGenApi.Model.DTOs
{
    public class ChatRequestDto
    {
        public string UserId { get; set; }
        public string Message { get; set; }
        public string SystemPrompt { get; set; } // Optional - only needed for first message
        public string Model { get; set; } // Optional - defaults to gpt-5
    }
}