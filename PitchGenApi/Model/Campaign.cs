// Model/Campaign.cs
using System.ComponentModel.DataAnnotations;

namespace PitchGenApi.Model
{
    /// Campaign entity class
    public class Campaign
    {
        public int Id { get; set; }
        public string CampaignName { get; set; }
        public int PromptId { get; set; }
        public string? ZohoViewId { get; set; }
        public int? SegmentId { get; set; }      // Add this
        public int ClientId { get; set; }

        // Navigation properties
        public virtual Segment? Segment { get; set; }
    }

    /// Model for creating a new Campaign
    public class CampaignCreateModel
    {
        [Required]
        public string CampaignName { get; set; }

        [Required]
        public int PromptId { get; set; }

        public string? ZohoViewId { get; set; }  // Make nullable

        public int? SegmentId { get; set; }      // Add this

        public string? Description { get; set; }   // ✅ Make nullable

        [Required]
        public int ClientId { get; set; }
    }

    /// Model for updating an existing Campaign
    public class CampaignUpdateModel
    {
        [Required]
        public int Id { get; set; }

        [Required]
        public string CampaignName { get; set; }

        [Required]
        public int PromptId { get; set; }

        public string? ZohoViewId { get; set; }  // Make nullable

        public int? SegmentId { get; set; }      // Add this
    }
}