namespace PitchGenApi.Model.DTOs
{
    public class InboxEmailDto
    {
        public long Id { get; set; }
        public string MessageId { get; set; }
        public string InReplyTo { get; set; }
        public string ThreadId { get; set; }
        public string FromEmail { get; set; }
        public string FromName { get; set; }
        public string Subject { get; set; }
        public string Body { get; set; }
        public DateTime Date { get; set; }
        public bool IsRead { get; set; }
        public string Provider { get; set; }
        public int? ContactId { get; set; }
        public Guid? Trackingid { get; set; }
        public DateTime? LastMessageDate { get; set; }
        public int ThreadCount { get; set; }
        public List<InboxMessageDto> Messages { get; set; }
    }

    public class InboxMessageDto
    {
        public string Type { get; set; }
        public DateTime? Date { get; set; }
        public string Body { get; set; }
    }
}
