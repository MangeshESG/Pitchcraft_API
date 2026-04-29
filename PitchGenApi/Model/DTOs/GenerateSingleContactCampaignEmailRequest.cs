namespace PitchGenApi.Model.DTOs
{
    public class GenerateSingleContactCampaignEmailRequest
    {
        public int BlueprintId { get; set; }
        public int ContactId { get; set; }
        public string ClientId { get; set; } = "";
        public bool OverwriteExisting { get; set; } = true;
    }
}
