using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Antiforgery;

namespace PitchGenApi.Model.DTOs
{
    public class SmtpCredentialDto 
    {
        
        [Required]
        public string OutgoingServer { get; set; }

        [Required]
        public int OutgoingPort { get; set; }

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
        public string OutgoingSecurityType { get; set; }

        [Required]
        public bool IsUpdate { get; set; }

        [Required]
        public string IncomingServer { get; set; }

        [Required]
        public int IncomingPort { get; set; }

        public bool FullInboxSync { get; set; } = true;

        public string IncomingSecurityType { get; set; }
    }

}
