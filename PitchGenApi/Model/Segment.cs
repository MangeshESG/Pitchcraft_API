namespace PitchGenApi.Model
{
    public class Segment
    {
        public int Id { get; set; }
        public int ClientId { get; set; }
        public string Name { get; set; } = default!;
        public string? Description { get; set; }
        public int? DataFileId { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? UpdatedAt { get; set; }

        public ICollection<SegmentContact> SegmentContacts { get; set; } = new List<SegmentContact>();
    }
}
