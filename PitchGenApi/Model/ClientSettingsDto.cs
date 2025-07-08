using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace PitchGenApi.Model
{
    public class ClientSettingsDto
    {
        public string Model_name { get; set; } // Match database column name
        public int Search_URL_count { get; set; } // Match database column name
        public string Search_term { get; set; } // Match database column name
        public string Instruction { get; set; } // Match database column name
        public string System_instruction { get; set; }


        [Required]
        [JsonPropertyName("subject_instructions")] // <-- Accepts subject_instructions from JSON
        public string Subject_instruction { get; set; }
    }
}
