using System.ComponentModel.DataAnnotations.Schema;
using System.ComponentModel.DataAnnotations;

namespace PitchGenApi.Model
{
    public class UnlockedContacts
    {
        [Key]
        public long Id { get; set; }

        public string ClientId { get; set; }

        public string ContactId { get; set; }

        public string EmailId { get; set; }

        public string LinkedInUrl { get; set; }

        public DateTime UnlockedOn { get; set; } = DateTime.Now;
    }
}
