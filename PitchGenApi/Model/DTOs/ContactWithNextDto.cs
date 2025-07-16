using PitchGenApi.Models;

namespace PitchGenApi.Model.DTOs
{
    public class ContactWithNextDto
    {
        public Contact CurrentContact { get; set; }
        public int? NextContactId { get; set; }
    }

}
