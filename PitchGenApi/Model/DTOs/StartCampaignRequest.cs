namespace PitchGenApi.Model.DTOs
{
    public class StartCampaignRequest
    {
        public string ClientId { get; set; } = string.Empty;
        public int TemplateDefinitionId { get; set; }
        public string TemplateName { get; set; } = string.Empty;
        public string? Model { get; set; }
        public int? SearchURLCount { get; set; }
        public string? SubjectInstructions { get; set; }
    }
}