namespace PitchGenApi.Model.DTOs
{
    public class EmailThreadDto
    {
        public Guid? TrackingId { get; set; }
        public string Subject { get; set; }
        public string ContactEmail { get; set; }
        public int TotalMessages { get; set; }
        public DateTime? LastMessageDate { get; set; }
        public bool HasUnread { get; set; }
        public int? ContactId { get; set; }
        public bool IsPinned { get; set; }
        public List<EmailConvDto> Messages { get; set; }
    }

    public class EmailConvDto
    {
        public string Type { get; set; } // Sent / Reply / Inbox

        public string MessageId { get; set; }

        public string Subject { get; set; }

        public string Body { get; set; }

        public string FromEmail { get; set; }

        public string ToEmail { get; set; }

        /// <summary>
        /// Every address on the message's To line, comma separated. Falls back
        /// to ToEmail for rows stored before the column existed.
        /// </summary>
        public string? ToRecipients { get; set; }

        /// <summary>The message's Cc line, comma separated.</summary>
        public string? CcEmail { get; set; }

        /// <summary>Bcc, on sent messages only — received mail never has it.</summary>
        public string? BccEmail { get; set; }

        public DateTime? Date { get; set; }

        public bool IsRead { get; set; }
        public bool IsAutoReply { get; set; }

        public int? ContactId { get; set; }

        public string? ContactName { get; set; }
        public string? Provider { get; set; }

        public int? Inboxid { get; set; }

        // =========================================
        // ATTACHMENTS
        // =========================================

        public List<EmailAttachmentDto> Attachments { get; set; }
            = new();
    }

    public class EmailAttachmentDto
    {
        public int Id { get; set; }

        public string MessageId { get; set; }

        public string FileName { get; set; }

        public string OriginalFileName { get; set; }

        public string ContentType { get; set; }

        public string FilePath { get; set; }

        public long? FileSize { get; set; }
    }
}
