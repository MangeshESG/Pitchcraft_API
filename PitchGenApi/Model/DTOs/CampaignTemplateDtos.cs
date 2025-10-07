// Model/DTOs/CampaignTemplateDtos.cs
using System;
using System.Collections.Generic;

namespace PitchGenApi.Model.DTOs
{
    public class SaveCampaignTemplateRequest
    {
        public string ClientId { get; set; }
        public string TemplateName { get; set; }
        public string SystemPrompt { get; set; }
        public string MasterPrompt { get; set; }
        public string PreviewText { get; set; }
        public string FinalPrompt { get; set; }
        public string FinalPreviewText { get; set; }
        public Dictionary<string, string> PlaceholderValues { get; set; }
        public string SelectedModel { get; set; }
        public List<ConversationMessage> ConversationMessages { get; set; }
    }

    public class ConversationMessage
    {
        public string Type { get; set; }
        public string Content { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class UpdateCampaignTemplateRequest
    {
        public int Id { get; set; }
        public string TemplateName { get; set; }
        public string SystemPrompt { get; set; }
        public string MasterPrompt { get; set; }
        public string PreviewText { get; set; }
        public string FinalPreviewText { get; set; }
        public string SelectedModel { get; set; }
        public Dictionary<string, string> PlaceholderValues { get; set; }
    }
}