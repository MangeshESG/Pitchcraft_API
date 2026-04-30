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
        public int? ContactId { get; set; }   // ✅ add

        public List<EmailConvDto> Messages { get; set; }
    }

    public class EmailConvDto
    {
        public string Type { get; set; } // Sent / Reply
        public string MessageId { get; set; }
        public string Subject { get; set; }
        public string Body { get; set; }
        public string FromEmail { get; set; }
        public string ToEmail { get; set; }
        public DateTime? Date { get; set; }
        public bool IsRead { get; set; }
        public int? ContactId { get; set; }   // ✅ add
        public string? ContactName { get; set; }   // ✅ add

    }
}
