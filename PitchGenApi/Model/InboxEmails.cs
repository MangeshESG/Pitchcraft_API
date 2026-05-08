namespace PitchGenApi.Model
{
    public class InboxEmails
    {
        public long Id { get; set; }
        public int ClientId { get; set; }
        public int InboxId { get; set; }

        public string MessageId { get; set; }
        public string? InReplyTo { get; set; }
        public string? ThreadId { get; set; }

        public string? FromEmail { get; set; }
        public string? Subject { get; set; }
        public string? Body { get; set; }
        public string? Provider { get; set; }

        public DateTime Date { get; set; }

        public bool IsRead { get; set; } = false;
        public bool IsDeleted { get; set; } = false;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
