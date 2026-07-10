namespace PitchGenApi.Model.DTOs
{
    public class BounceParseResult
    {
        public bool IsBounce { get; set; }

        public string? OriginalMessageId { get; set; }

        public string? RecipientEmail { get; set; }

        public string? Action { get; set; }

        public string? StatusCode { get; set; }

        public string? DiagnosticCode { get; set; }

        public string? RemoteServer { get; set; }

        public string? Reason { get; set; }

        public string? BounceType { get; set; }
    }
}
