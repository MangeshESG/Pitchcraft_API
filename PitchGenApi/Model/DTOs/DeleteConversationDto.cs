namespace PitchGenApi.Model.DTOs
{
    public class DeleteConversationDto
    {
        public List<Guid> TrackingIds { get; set; }
        public string DeleteMode { get; set; }
        public int clientid { get; set; }
    }
}
