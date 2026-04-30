namespace PitchGenApi.Model.DTOs
{
    public class ContactEmailConversationContextDto
    {
        public int ClientId { get; set; }
        public int ContactId { get; set; }
        public string? FullName { get; set; }
        public string? Email { get; set; }
        public DateTime? ContactCreatedAt { get; set; }
        public List<ConversationEmailDto> Emails { get; set; } = new();
        public string PromptContext { get; set; } = string.Empty;
    }

    public class ConversationEmailDto
    {
        public int EmailLogId { get; set; }
        public string? MessageId { get; set; }
        public DateTime? SentAt { get; set; }
        public string? SenderName { get; set; }
        public string? SenderEmailId { get; set; }
        public string? RecipientName { get; set; }
        public string? ToEmail { get; set; }
        public string? Subject { get; set; }
        public string? Body { get; set; }
        public List<EmailReplyDto> Replies { get; set; } = new();
    }

}
