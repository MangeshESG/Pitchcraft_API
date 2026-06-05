namespace PitchGenApi.Model.DTOs
{
    public class UpdateEmailSignatureDto
    {
        public int Id { get; set; }

        public string SignatureName { get; set; }

        public string SignatureHtml { get; set; }
        public bool IsDefault { get; set; }
    }
}
