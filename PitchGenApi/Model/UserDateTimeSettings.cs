using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PitchGenApi.Model
{
    [Table("UserDateTimeSettings")]
    public class UserDateTimeSettings
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }

        public int ClientId { get; set; }

        [MaxLength(100)]
        public string TimeZone { get; set; } = "Asia/Kolkata";

        [MaxLength(200)]
        public string? TimeZoneLabel { get; set; }

        [MaxLength(20)]
        public string DateFormat { get; set; } = "DD-MM-YYYY";

        [MaxLength(10)]
        public string TimeFormat { get; set; } = "24";

        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }
}
