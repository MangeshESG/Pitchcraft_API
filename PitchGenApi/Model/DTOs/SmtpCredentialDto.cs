using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Antiforgery;

namespace PitchGenApi.Model.DTOs
{
    public class SmtpCredentialDto 
    {
        
        public int Id { get; set; }

        [Required]
        public string Server { get; set; }

        [Required]
        public int Port { get; set; }

        [Required]
        public string Username { get; set; }

        [Required]
        public string Password { get; set; }

        [Required]
        public string FromEmail { get; set; }

        [Required]
        public bool UseSsl { get; set; }
    }

}
