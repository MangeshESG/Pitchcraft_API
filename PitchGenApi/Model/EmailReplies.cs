namespace PitchGenApi.Model
{
    public class EmailReplies
    {
        public int Id { get; set; }
        public int ClientId { get; set; }
        public int? ContactId { get; set; }
        public int? CampaignId { get; set; }
        public int? Inboxid { get; set; }

        public string? MessageId { get; set; }
        public string? InReplyTo { get; set; }
        public string? Provider { get; set; }
        public string? FromEmail { get; set; }
        public string? FromName { get; set; }
        /// <summary>The mailbox that received this reply (this inbox).</summary>
        public string? ToEmail { get; set; }
        /// <summary>
        /// Every address on the message's To line, comma separated, as the
        /// provider delivered it. ToEmail stays the receiving mailbox, which
        /// bounce matching and thread queries rely on.
        /// </summary>
        public string? ToRecipients { get; set; }
        /// <summary>The message's Cc line, comma separated. Bcc is never
        /// visible on received mail, so there is no column for it.</summary>
        public string? CcEmail { get; set; }
        public string? Subject { get; set; }
        public string? Body { get; set; }
        public string? ThreadId { get; set; }
        public Guid? TrackingId { get; set; }

        public DateTime? Date { get; set; }
        public bool? IsRead { get; set; } = false;
        public bool? IsDeleted { get; set; } = false;
        public bool IsAutoReply { get; set; }

        public DateTime? CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
