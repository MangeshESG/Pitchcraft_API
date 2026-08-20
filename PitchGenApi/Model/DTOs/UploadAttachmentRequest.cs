namespace PitchGenApi.Model.DTOs
{
    public class UploadAttachmentRequest
    {
        public int ContactId { get; set; }

        public int ClientId { get; set; }

        public string Name { get; set; }

        public string? Description { get; set; }

        public IFormFile File { get; set; }
    }
}
