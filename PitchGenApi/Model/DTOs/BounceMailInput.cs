namespace PitchGenApi.Model.DTOs
{
    public class BounceMailInput
    {
        public string? FromEmail { get; set; }

        public string? Subject { get; set; }

        public string? Body { get; set; }

        public string? HeadersText { get; set; }

        public string? Provider { get; set; }
    }
}
