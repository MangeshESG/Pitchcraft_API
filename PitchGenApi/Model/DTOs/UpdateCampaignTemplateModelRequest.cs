using System.ComponentModel.DataAnnotations;

namespace PitchGenApi.Model.DTOs
{
    public class UpdateCampaignTemplateModelRequest
    {
        [Required]
        public int TemplateId { get; set; }

        [Required]
        [MaxLength(100)]
        public string SelectedModel { get; set; } = string.Empty;
    }
}
