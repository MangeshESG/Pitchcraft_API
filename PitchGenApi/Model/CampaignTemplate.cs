// Models/CampaignTemplate.cs
using System;
using System.ComponentModel.DataAnnotations;

namespace PitchGenApi.Models
{
    // In your Models folder, update CampaignTemplate.cs
    public class CampaignTemplate
    {
        public int Id { get; set; }
        public string ClientId { get; set; }
        public string TemplateName { get; set; }

        // Updated column names
        public string AIInstructions { get; set; } // Previously SystemPrompt
        public string PlaceholderListInfo { get; set; } // Previously MasterPrompt
        public string MasterBlueprintUnpopulated { get; set; } // Previously PreviewText
        public string PlaceholderListWithValue { get; set; } // Previously FinalPrompt
        public string CampaignBlueprint { get; set; } // Previously FinalPreviewText

        public string PlaceholderValues { get; set; }
        public string SelectedModel { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }

        // Navigation property
        public CampaignConversation Conversation { get; set; }
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