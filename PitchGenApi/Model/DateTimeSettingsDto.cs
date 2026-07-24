using System.ComponentModel.DataAnnotations;

namespace PitchGenApi.Model
{
    public class DateTimeSettingsDto
    {
        [Required]
        public string TimeZone { get; set; }
        public string? TimeZoneLabel { get; set; }

        [RegularExpression("^(DD-MM-YYYY|MM-DD-YYYY)$")]
        public string DateFormat { get; set; } = "DD-MM-YYYY";

        [RegularExpression("^(12|24)$")]
        public string TimeFormat { get; set; } = "24";
    }
}
