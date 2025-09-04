namespace PitchGenApi.Model
{
    public class CampaignSession
    {
        public string UserId { get; set; } = string.Empty;
        public List<Dictionary<string, string>> Messages { get; set; } = new();
        public string? CampaignPrompt { get; set; }
        public string? DraftEmail { get; set; }
        public bool IsApproved { get; set; }
    }
}