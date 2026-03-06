namespace PitchGenApi.Model
{
    public class ContactAttachments
    {
        public int Id { get; set; }
        public int ContactId { get; set; }
        public string FileName { get; set; }
        public string Description { get; set; }
        public string FileUrl { get; set; }
        public long FileSize { get; set; }
        public DateTime CreatedDate { get; set; }
    }
}
