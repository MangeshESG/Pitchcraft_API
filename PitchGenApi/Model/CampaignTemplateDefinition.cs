// Models/CampaignTemplateDefinition.cs
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace PitchGenApi.Models
{
    public class CampaignTemplateDefinition
    {
        public int Id { get; set; }

        [Required]
        [MaxLength(255)]
        public string TemplateName { get; set; } = string.Empty;

        public string? AIInstructions { get; set; }

        // ✅ NEW FIELD - AI Instructions specifically for editing placeholders
        public string? AIInstructionsForEdit { get; set; }

        public string? PlaceholderList { get; set; }

        public string? PlaceholderListExtensive { get; set; }

        public string? MasterBlueprintUnpopulated { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public DateTime? UpdatedAt { get; set; }

        public string? CreatedBy { get; set; }

        public bool IsActive { get; set; } = true;
        public int? SearchURLCount { get; set; }
        public string? SubjectInstructions { get; set; }

        // Navigation property
        public ICollection<CampaignTemplate> CampaignTemplates { get; set; } = new List<CampaignTemplate>();
    }
}