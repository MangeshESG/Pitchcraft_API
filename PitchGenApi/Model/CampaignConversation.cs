using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PitchGenApi.Models
{
    public class CampaignConversation
    {
        public int Id { get; set; }

        [Required]
        public string ClientId { get; set; } = string.Empty;

        public int? CampaignTemplateId { get; set; }

        public string? ConversationData { get; set; }

        public string? Model { get; set; }

        public DateTime? StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }


        public bool IsComplete { get; set; } = false;
        public string Mode { get; set; } = "new";  // new or edit
        public int EditNumber { get; set; } = 0;   // increments on each edit
        // Navigation property
        [ForeignKey("CampaignTemplateId")]
        public CampaignTemplate? CampaignTemplate { get; set; }
    }
}