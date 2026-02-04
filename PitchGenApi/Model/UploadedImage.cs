namespace PitchGenApi.Models
{
    public class UploadedImage
    {
        public int Id { get; set; }

        public string ClientId { get; set; } = string.Empty;

        public string FileName { get; set; } = string.Empty;

        public string Url { get; set; } = string.Empty;

        public string ContentType { get; set; } = string.Empty;

        public long SizeInBytes { get; set; }

        public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
    }
}
