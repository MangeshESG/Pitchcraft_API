namespace PitchGenApi.Model
{
    public class EmailSignatures
    {
        public int Id { get; set; }

        public int ClientId { get; set; }

        public int OutboxId { get; set; }

        public string SignatureName { get; set; }

        public string SignatureHtml { get; set; }
        public string Provider { get; set; }

        public bool IsDefault { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public DateTime? UpdatedAt { get; set; }
    }
}
