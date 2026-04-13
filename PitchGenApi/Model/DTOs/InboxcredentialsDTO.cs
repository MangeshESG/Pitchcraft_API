using System.ComponentModel.DataAnnotations;

namespace PitchGenApi.Model.DTOs
{
    public class InboxcredentialsDTO
    {
        [Required]
        public int ClientId { get; set; }

        [Required, EmailAddress]
        public string EmailAddress { get; set; }

        [Required]
        [RegularExpression("POP3|IMAP", ErrorMessage = "Protocol must be POP3 or IMAP")]
        public string Protocol { get; set; }

        [Required]
        public string Host { get; set; }

        [Required]
        public int Port { get; set; }

        public bool UseSSL { get; set; } = true;

        [Required]
        public string Username { get; set; }

        [Required]
        public string Password { get; set; } // API me encrypted store karenge

        //public int SyncIntervalMinutes { get; set; } = 5;
    }
}
