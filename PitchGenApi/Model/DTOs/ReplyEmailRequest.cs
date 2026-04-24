namespace PitchGenApi.Model.DTOs
{
    public class ReplyEmailRequest
    {
        public Guid TrackingId { get; set; }
        public int ClientId { get; set; }
        public string ReplyBody { get; set; }
        public int Outboxid { get; set; }
        public string BccEmail { get; set; }
        public string Provider { get; set; }
    }
}
