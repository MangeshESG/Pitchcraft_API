namespace PitchGenApi.Model.DTOs
{
    public class ContactQAMessageDto
    {
        public string Role { get; set; } = "";
        public string Content { get; set; } = "";
    }

    public class ContactQARequest
    {
        public int ClientId { get; set; }
        public int ContactId { get; set; }
        public string ModelName { get; set; } = "gpt-5.1";
        public string Question { get; set; } = "";
        public object? Context { get; set; }
        public string? ContextSummary { get; set; }
        public List<ContactQAMessageDto> Messages { get; set; } = new();
    }

    public class ContactQAResponse
    {
        public bool IsSuccess { get; set; }
        public string Answer { get; set; } = "";
        public int PromptTokens { get; set; }
        public int CompletionTokens { get; set; }
        public int TotalTokens { get; set; }
        public decimal CurrentCost { get; set; }
    }

}
