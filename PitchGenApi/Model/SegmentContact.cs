using PitchGenApi.Models;

namespace PitchGenApi.Model
{
    public class SegmentContact
    {
        public int SegmentId { get; set; }
        public int ContactId { get; set; }
        public DateTime AddedAt { get; set; } = DateTime.UtcNow;

        public Segment Segment { get; set; } = default!;
        public Contact Contact { get; set; } = default!;
    }
}
