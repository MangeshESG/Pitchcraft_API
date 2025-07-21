using System.ComponentModel.DataAnnotations;

namespace PitchGenApi.Model
{
    public class ProcessRequest
    {
        [Required]
        public string SearchTerm { get; set; }

        [Required]
        public string Instructions { get; set; }

        [Required]
        public string ModelName { get; set; }

        [Range(1, int.MaxValue, ErrorMessage = "SearchCount must be greater than 0")]
        public int SearchCount { get; set; }
    }
}
