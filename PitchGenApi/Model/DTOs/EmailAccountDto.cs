namespace PitchGenApi.Model.DTOs
{
    public class EmailAccountDto
    {
        public int Id { get; set; }

        public string Email { get; set; }

        public string Provider { get; set; }

        public string? SenderName { get; set; }

        public bool FullInboxSync { get; set; }

        public DateTime? CreatedAt { get; set; }
    }
}
