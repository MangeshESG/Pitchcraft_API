namespace PitchGenApi.Model.DTOs
{
    public class ReplyEmailRequest
    {
        public Guid TrackingId { get; set; }
        public int ClientId { get; set; }
        public string ReplyBody { get; set; }
        public int Outboxid { get; set; }
        public string Provider { get; set; }
        public string? CC { get; set; }
        public string? BCC { get; set; }
        public List<IFormFile>? Attachments { get; set; }

    }
}
