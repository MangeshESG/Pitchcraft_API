using System;
using System.Collections.Generic;

namespace PitchGenApi.Model.DTOs
{
    // DTO for saving a new template definition (admin operation)
    public class SaveTemplateDefinitionRequest
    {
        public string TemplateName { get; set; } = string.Empty;
        public string? AIInstructions { get; set; }
        public string? AIInstructionsForEdit { get; set; }
        public string? PlaceholderList { get; set; }
        public string? PlaceholderListExtensive { get; set; }
        public string? MasterBlueprintUnpopulated { get; set; }
        public string? CreatedBy { get; set; }
        public int? SearchURLCount { get; set; }
        public string? SubjectInstructions { get; set; }
        public string SelectedModel { get; set; }


    }

    // DTO for updating template definition
    public class UpdateTemplateDefinitionRequest
    {
        public int Id { get; set; }
        public string TemplateName { get; set; } = string.Empty;
        public string? AIInstructions { get; set; }
        public string? AIInstructionsForEdit { get; set; }
        public string? PlaceholderList { get; set; }
        public string? PlaceholderListExtensive { get; set; }
        public string? MasterBlueprintUnpopulated { get; set; }
        public int? SearchURLCount { get; set; }
        public string? SubjectInstructions { get; set; }
        public string SelectedModel { get; set; }


    }

    // DTO for saving client's filled campaign
    public class SaveCampaignTemplateRequest
    {
        public string ClientId { get; set; } = string.Empty;
        public int TemplateDefinitionId { get; set; }

        // Filled data
        public string? PlaceholderListWithValue { get; set; }
        public string? CampaignBlueprint { get; set; }
        public Dictionary<string, string>? PlaceholderValues { get; set; }

        public string? SelectedModel { get; set; }
        public List<ConversationMessage>? ConversationMessages { get; set; }

        public int? SearchURLCount { get; set; }
        public string? SubjectInstructions { get; set; }

    }

    // DTO for updating client's campaign
    public class UpdateCampaignTemplateRequest
    {
        public int Id { get; set; }
        public string? PlaceholderListWithValue { get; set; }
        public string? CampaignBlueprint { get; set; }
        public Dictionary<string, string>? PlaceholderValues { get; set; }
        public string? SelectedModel { get; set; }
        public int? SearchURLCount { get; set; }
        public string? SubjectInstructions { get; set; }

    }

    // Response DTO with full details
    public class CampaignTemplateDetailResponse
    {
        public int Id { get; set; }
        public string ClientId { get; set; } = string.Empty;

        // From definition
        public int TemplateDefinitionId { get; set; }
        public string TemplateName { get; set; } = string.Empty;
        public string? AIInstructions { get; set; }
        public string? AIInstructionsForEdit { get; set; }
        public string? PlaceholderList { get; set; }
        public string? PlaceholderListExtensive { get; set; }
        public string? MasterBlueprintUnpopulated { get; set; }

        // Client-specific data
        public string? PlaceholderListWithValue { get; set; }
        public string? CampaignBlueprint { get; set; }
        public Dictionary<string, string>? PlaceholderValues { get; set; }
        public string? SelectedModel { get; set; }

        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }


        public int? SearchURLCount { get; set; }
        public string? SubjectInstructions { get; set; }
        public ConversationData? Conversation { get; set; }
    }

    public class ConversationData
    {
        public List<ConversationMessage> Messages { get; set; } = new();
        public DateTime StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
    }

    public class ConversationMessage
    {
        public string Type { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
    }
}