// Model/Campaign.cs
using System.ComponentModel.DataAnnotations;

namespace PitchGenApi.Model
{
    /// Campaign entity class
    public class Campaign
    {
        public int Id { get; set; }

        // Non-nullable string, must always be set
        public string CampaignName { get; set; } = string.Empty;

        // Nullable because prompt may not exist (template-driven campaign)
        public int? PromptId { get; set; }

        // Nullable because ZohoView or Segment are mutually exclusive
        public string? ZohoViewId { get; set; }

        public int? SegmentId { get; set; }

        // Every campaign must belong to a client
        public int ClientId { get; set; }

        // ✅ Add missing field
        public string? Description { get; set; }

        // ✅ New TemplateId field
        public int? TemplateId { get; set; }

        // Optional: Date tracking
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }


    /// Model for creating a new Campaign
    public class CampaignCreateModel
    {
        public string CampaignName { get; set; } = string.Empty;
        public int? PromptId { get; set; }
        public int ClientId { get; set; }
        public string? ZohoViewId { get; set; }
        public int? SegmentId { get; set; }
        public string? Description { get; set; }
        public int? TemplateId { get; set; }
    }



/// Model for updating an existing Campaign
public class CampaignUpdateModel
    {
        [Required]
        public int Id { get; set; }

        [Required]
        public string CampaignName { get; set; }

        public int? PromptId { get; set; }

        public string? ZohoViewId { get; set; }  // Make nullable
        public string? Description { get; set; }  // Make nullable
        public int? TemplateId { get; set; }  // Make nullable

        public int? SegmentId { get; set; }      // Add this
    }
}