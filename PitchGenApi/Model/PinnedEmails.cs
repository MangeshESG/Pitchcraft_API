namespace PitchGenApi.Model
{
    public class PinnedEmails
    {
        public int Id { get; set; }

        public Guid TrackingId { get; set; }

        public int ClientId { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
