// Models/CampaignTemplate.cs
using System;
using System.ComponentModel.DataAnnotations;

namespace PitchGenApi.Models
{
    public class CampaignTemplate
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public string ClientId { get; set; }

        [Required]
        public string TemplateName { get; set; }

        public string SystemPrompt { get; set; }
        public string MasterPrompt { get; set; }
        public string PreviewText { get; set; }
        public string FinalPrompt { get; set; }
        public string FinalPreviewText { get; set; }
        public string PlaceholderValues { get; set; } // JSON string
        public string SelectedModel { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }

        // Navigation property
        public virtual CampaignConversation Conversation { get; set; }
    }

    public class CampaignConversation
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public string ClientId { get; set; }

        public int? CampaignTemplateId { get; set; }
        public string ConversationData { get; set; } // JSON string of messages
        public string Model { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public bool IsComplete { get; set; }

        // Navigation property
        public virtual CampaignTemplate CampaignTemplate { get; set; }
    }
}