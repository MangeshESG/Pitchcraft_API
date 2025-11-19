using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PitchGenApi.Models
{
    [Table("CampaignTemplates")]
    public class CampaignTemplate
    {
        [Key]
        public int Id { get; set; }

        [Required]
        [MaxLength(255)]
        public string ClientId { get; set; } = string.Empty;

        [Required]
        public int TemplateDefinitionId { get; set; }

        // ✅ New – instance‑specific campaign name
        [MaxLength(255)]
        public string TemplateName { get; set; } = string.Empty;

        // Data gathered from chat session
        public string? PlaceholderListWithValue { get; set; }

        public string? CampaignBlueprint { get; set; }

        public string? PlaceholderValues { get; set; }

        [MaxLength(50)]
        public string SelectedModel { get; set; } = "gpt-5";

        // ✅ New – stores live example HTML output
        public string? ExampleOutput { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public DateTime? UpdatedAt { get; set; }

        public int? SearchURLCount { get; set; }
        public string? SubjectInstructions { get; set; }

        // Navigation properties
        [ForeignKey(nameof(TemplateDefinitionId))]
        public virtual CampaignTemplateDefinition? TemplateDefinition { get; set; }

        public virtual CampaignConversation? Conversation { get; set; }
    }
}