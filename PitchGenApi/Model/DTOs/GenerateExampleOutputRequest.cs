namespace PitchGenApi.Model.DTOs
{
    public class GenerateExampleOutputRequest
    {
        public string UserId { get; set; }
        public int CampaignTemplateId { get; set; }
        public string? Model { get; set; }
    }
}