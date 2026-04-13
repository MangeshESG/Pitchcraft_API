namespace PitchGenApi.Model
{
    public class EmailReplies
    {
        public int Id { get; set; }
        public int ClientId { get; set; }
        public int? ContactId { get; set; }
        public int? CampaignId { get; set; }

        public string MessageId { get; set; }
        public string InReplyTo { get; set; }

        public string FromEmail { get; set; }
        public string Subject { get; set; }
        public string Body { get; set; }
        public Guid? TrackingId { get; set; }

        public DateTime Date { get; set; }
        public bool IsRead { get; set; } = false;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
