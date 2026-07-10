namespace PitchGenApi.Model
{
    public class EmailBounce
    {
        public long Id { get; set; }

        public int ClientId { get; set; }

        public Guid? TrackingId { get; set; }

        public long? EmailLogId { get; set; }

        public string? BounceMessageId { get; set; }

        public string? OriginalMessageId { get; set; }

        public string? RecipientEmail { get; set; }

        public string? BounceType { get; set; }

        public string? Action { get; set; }

        public string? StatusCode { get; set; }

        public string? DiagnosticCode { get; set; }

        public string? RemoteServer { get; set; }

        public string? Reason { get; set; }

        public string? Provider { get; set; }

        public DateTime BounceDate { get; set; }

        public string? RawHeaders { get; set; }

        public string? RawBody { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
