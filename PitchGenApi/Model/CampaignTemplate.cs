using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PitchGenApi.Models
{
    public class CampaignTemplate
    {
        public int Id { get; set; }

        [Required]
        public string ClientId { get; set; } = string.Empty;

        [Required]
        public int TemplateDefinitionId { get; set; }

        // Client-specific filled data
        public string? PlaceholderListWithValue { get; set; }

        public string? CampaignBlueprint { get; set; }

        public string? PlaceholderValues { get; set; } // JSON string

        public string? SelectedModel { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public DateTime? UpdatedAt { get; set; }

        // Navigation properties
        [ForeignKey("TemplateDefinitionId")]
        public CampaignTemplateDefinition? TemplateDefinition { get; set; }

        public CampaignConversation? Conversation { get; set; }
    }
}