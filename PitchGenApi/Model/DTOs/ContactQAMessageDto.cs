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

        /// <summary>
        /// Which record sources the user ticked, keyed exactly as the UI keys
        /// them (profile, notes, emails, linkedinProfile, linkedinMessages,
        /// savedResearch, internet). The context packet arrives already
        /// filtered; this is only so the prompt can name what was left out
        /// instead of letting the model treat a missing source as "none exists".
        /// </summary>
        public Dictionary<string, bool>? Sources { get; set; }

        /// <summary>
        /// Whether the model may reach for the live web. Null means "not sent",
        /// which keeps the old always-on behaviour for any caller that predates
        /// the Internet research checkbox.
        /// </summary>
        public bool? AllowWebSearch { get; set; }
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
