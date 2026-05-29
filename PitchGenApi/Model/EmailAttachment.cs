namespace PitchGenApi.Model
{
    public class EmailAttachment
    {
        public int Id { get; set; }

        public string MessageId { get; set; }

        public string FileName { get; set; }

        public string OriginalFileName { get; set; }

        public string ContentType { get; set; }

        public string FilePath { get; set; }

        public long? FileSize { get; set; }

        public string Provider { get; set; }

        public DateTime CreatedAt { get; set; }
    }
}
