namespace PitchGenApi.Model.DTOs
{
    public class ForwardEmailDto
    {
        public Guid TrackingId { get; set; }

        public int ClientId { get; set; }

        public string ForwardToEmail { get; set; }
        public string Provider { get; set; }

        public string ForwardMessage { get; set; }

        public int OutboxId { get; set; }

        public string? CcEmail { get; set; } = "";
        public string? BccEmail { get; set; } = "";
    }
}
