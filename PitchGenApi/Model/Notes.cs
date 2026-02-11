using System.ComponentModel.DataAnnotations;

namespace PitchGenApi.Model
{
    public class Notes
    {
        public int Id { get; set; }
        public int ClientId { get; set; }
        public int ContactId { get; set; }
        public string Note { get; set; }
        public bool IsPin { get; set; }
        public bool IsUseInGenration { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
    }
}
