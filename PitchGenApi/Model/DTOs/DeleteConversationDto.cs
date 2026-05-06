namespace PitchGenApi.Model.DTOs
{
    public class DeleteConversationDto
    {
        public Guid TrackingId { get; set; }
        public string DeleteMode { get; set; }
        public int clientid { get; set; }
    }
}
