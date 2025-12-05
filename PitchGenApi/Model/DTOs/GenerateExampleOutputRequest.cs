namespace PitchGenApi.Model.DTOs
{
    public class GenerateExampleOutputRequest
    {
        public string UserId { get; set; }
        public int CampaignTemplateId { get; set; }
        public string? Model { get; set; }
        public Dictionary<string, string>? PlaceholderValues { get; set; }   // ✅ NEW

    }

    public class ExampleOutputResult
    {
        public string FilledTemplate { get; set; } = "";
        public string? HtmlOutput { get; set; }
    }

}