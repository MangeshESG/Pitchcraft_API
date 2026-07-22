namespace PitchGenApi.Model.DTOs
{
    public class BounceSaveInput
    {
        public int ClientId { get; set; }

        public int? InboxId { get; set; }

        public Guid? TrackingId { get; set; }

        public BounceParseResult BounceInfo { get; set; }

        public string BounceMessageId { get; set; }

        public string? AlternateBounceMessageId { get; set; }

        public string? InReplyTo { get; set; }

        public List<string> ReferenceIds { get; set; } = new();

        public string? Provider { get; set; }

        public DateTime BounceDate { get; set; }

        public string? RawHeaders { get; set; }

        public string? RawBody { get; set; }
    }
}
