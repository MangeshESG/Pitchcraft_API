
namespace PitchGenApi.Model
{
    public class DomainEmailVerification
    {
        public int Id { get; set; }

        public int DomainId { get; set; }
        public int ClientId { get; set; }

        public string Email { get; set; }

        public bool IsEmailVerified { get; set; }

        public DateTime? EmailVerifiedAt { get; set; }

        public DateTime CreatedAt { get; set; }       
    }
}
