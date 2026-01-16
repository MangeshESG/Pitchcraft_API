using System.ComponentModel.DataAnnotations;

namespace PitchGenApi.Model
{
    public class DomainVerification
    {
        public int Id { get; set; }
        public int ClientId { get; set; }
        public string Domain { get; set; }
        public string VerificationToken { get; set; }
        public bool IsVerified { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? VerifiedAt { get; set; }
        public bool IsSpfVerified { get; set; } = false;
        public bool IsDkimVerified { get; set; } = false;
        public bool IsDmarcVerified { get; set; } = false;
    }
}
