using System.ComponentModel.DataAnnotations;

namespace PitchGenApi.Model
{
    public class Inboxcredentials
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public int ClientId { get; set; }

        [Required, EmailAddress]
        public string EmailAddress { get; set; }

        [Required]
        [RegularExpression("POP3|IMAP")]
        public string Protocol { get; set; }

        [Required]
        public string Host { get; set; }

        [Required]
        public int Port { get; set; }

        public bool UseSSL { get; set; } = true;

        [Required]
        public string Username { get; set; }

        [Required]
        public string Password { get; set; }
        public long LastUid { get; set; } // ✅ NEW FIELD
        public int SyncIntervalMinutes { get; set; } = 5;
        public int? Outboxid { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
