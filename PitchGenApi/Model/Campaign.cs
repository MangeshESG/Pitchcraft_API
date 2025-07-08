// Model/Campaign.cs
using System.ComponentModel.DataAnnotations;

namespace PitchGenApi.Model
{
    /// <summary>
    /// Campaign entity class
    /// </summary>
    public class Campaign
    {
        public int Id { get; set; }
        public string CampaignName { get; set; }
        public int PromptId { get; set; }
        public string ZohoViewId { get; set; }
        public int ClientId { get; set; }
    }

    /// <summary>
    /// Model for creating a new Campaign
    /// </summary>
    public class CampaignCreateModel
    {
        [Required(ErrorMessage = "Campaign name is required")]
        [StringLength(255, ErrorMessage = "Campaign name cannot exceed 255 characters")]
        public string CampaignName { get; set; }

        [Required(ErrorMessage = "Prompt ID is required")]
        public int PromptId { get; set; }

        [Required(ErrorMessage = "Zoho View ID is required")]
        [StringLength(50, ErrorMessage = "Zoho View ID cannot exceed 50 characters")]
        public string ZohoViewId { get; set; }

        [Required(ErrorMessage = "Client ID is required")]
        public int ClientId { get; set; }
    }

    /// <summary>
    /// Model for updating an existing Campaign
    /// </summary>
    public class CampaignUpdateModel
    {
        [Required]
        public int Id { get; set; }

        [Required]
        [StringLength(100)]
        public string CampaignName { get; set; }

        [Required]
        public int PromptId { get; set; }

        [Required]
        public string ZohoViewId { get; set; }
    }
}