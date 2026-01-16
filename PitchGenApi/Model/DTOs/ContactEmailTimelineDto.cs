namespace PitchGenApi.Model.DTOs
{
    public class ContactEmailTimelineDto
    {
        public int ContactId { get; set; }
        public string FullName { get; set; }
        public string Email { get; set; }
        public DateTime ContactCreatedAt { get; set; }

        public List<SentEmailDto> Emails { get; set; }
    }

    public class EmailEventDto
    {
        public string EventType { get; set; }
        public DateTime EventAt { get; set; }
        public string TargetUrl { get; set; }
    }

    public class SentEmailDto
    {
        public string TrackingId { get; set; }
        public DateTime? SentAt { get; set; }
        public string SenderEmailId { get; set; }
        public string Subject { get; set; }
        public string Body { get; set; }
        public string Source { get; set; }

        public List<EmailEventDto> Events { get; set; }
    }

   

}
