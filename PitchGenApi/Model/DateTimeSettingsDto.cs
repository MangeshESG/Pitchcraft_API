using System.ComponentModel.DataAnnotations;

namespace PitchGenApi.Model
{
    public class DateTimeSettingsDto
    {
        [Required]
        public string TimeZone { get; set; }
        public string? TimeZoneLabel { get; set; }

        // "DD MMM YYYY" renders as "10 May 2027". A month name reads the same
        // way everywhere, so it is what an account gets until it says otherwise.
        [RegularExpression("^(DD MMM YYYY|DD-MM-YYYY|MM-DD-YYYY)$")]
        public string DateFormat { get; set; } = "DD MMM YYYY";

        [RegularExpression("^(12|24)$")]
        public string TimeFormat { get; set; } = "24";
    }
}
