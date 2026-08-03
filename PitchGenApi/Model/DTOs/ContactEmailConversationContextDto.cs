namespace PitchGenApi.Model.DTOs
{
    public class ContactEmailConversationContextDto
    {
        public int ClientId { get; set; }
        public int ContactId { get; set; }
        public string? FullName { get; set; }
        public string? Email { get; set; }
        public DateTime? ContactCreatedAt { get; set; }

        // Every message of the conversation, oldest first — outbound and
        // inbound merged into a single chronological list.
        public List<ConversationEmailDto> Emails { get; set; } = new();

        public int TotalCount { get; set; }
        public int SentCount { get; set; }
        public int ReceivedCount { get; set; }

        public string PromptContext { get; set; } = string.Empty;
    }

    public class ConversationEmailDto
    {
        public int EmailLogId { get; set; }

        // "EmailLog" (sent by us), "EmailReply" or "InboxEmail" (mailbox sync).
        public string Source { get; set; } = "";
        public long SourceId { get; set; }

        // "Sent" = from us to the contact, "Received" = from the contact to us.
        public string Direction { get; set; } = "Sent";

        public string? MessageId { get; set; }
        public string? InReplyTo { get; set; }
        public string? ThreadId { get; set; }
        public Guid? TrackingId { get; set; }

        public DateTime? SentAt { get; set; }
        public string? SenderName { get; set; }
        public string? SenderEmailId { get; set; }
        public string? RecipientName { get; set; }
        public string? ToEmail { get; set; }
        public string? Subject { get; set; }
        public string? Body { get; set; }

        // Kept so callers written against the old "sent email with nested
        // replies" shape keep working; every reply is also a top-level entry
        // in Emails.
        public List<EmailReplyDto> Replies { get; set; } = new();
    }

}
