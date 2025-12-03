namespace PitchGenApi.Model
{
    public class UnsubscribedContacts
    {
        public int Id { get; set; }
        public int ClientId { get; set; }
        public string Email { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}
