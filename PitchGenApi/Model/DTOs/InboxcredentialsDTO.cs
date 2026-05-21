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
        public string Host { get; set; }

        [Required]
        public int Port { get; set; }

        public bool FullInboxSync { get; set; } = true;

        [Required]
        public string Username { get; set; }

        [Required]
        public string Password { get; set; }
        public string encryption { get; set; }


        //public int SyncIntervalMinutes { get; set; } = 5;
    }
}
