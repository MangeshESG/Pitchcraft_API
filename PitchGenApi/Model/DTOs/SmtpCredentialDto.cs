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
        public int DomainId { get; set; }

        [Required]
        public string Username { get; set; }

        [Required]
        public string Password { get; set; }

        [Required]
        public string FromEmail { get; set; }
        [Required]
        public string SenderName { get; set; }

        [Required]
        public bool UseSsl { get; set; }
        
        [Required]
        public string SecurityType { get; set; }

        [Required]
        public bool IsUpdate { get; set; }
    }

}
