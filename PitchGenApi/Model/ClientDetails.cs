using System.ComponentModel.DataAnnotations;

namespace PitchGenApi.Model
{
    public class ClientDetails
    {
        [Key]  
        public int Id { get; set; }
        public string FirstName { get; set; }
        public bool IsAdmin { get; set; }
        public string LastName { get; set; }
        public string Email { get; set; }
        public string Username { get; set; }
        public string PasswordHash { get; set; }
        public string CompanyName { get; set; }
        public string JobTitle { get; set; }
        public int? TrustDiviceNumber { get; set; }
        public DateTime? TrustExpiry { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}
