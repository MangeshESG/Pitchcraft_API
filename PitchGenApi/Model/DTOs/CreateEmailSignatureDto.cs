namespace PitchGenApi.Model.DTOs
{
    public class CreateEmailSignatureDto
    {
        public int ClientId { get; set; }

        public int OutboxId { get; set; }

        public string SignatureName { get; set; }

        public string SignatureHtml { get; set; }
        public string Provider { get; set; }

        public bool IsDefault { get; set; }
    }
}
