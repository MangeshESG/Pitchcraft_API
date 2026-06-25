namespace PitchGenApi.Model
{
    public class KraftHistory
    {
        public int Id { get; set; }

        public int ContactId { get; set; }

        public int ClientId { get; set; }

        public int? CampaignId { get; set; }

        public int? BlueprintId { get; set; }
        public string Process { get; set; }

        public DateTime KraftedDate { get; set; } = DateTime.UtcNow;
    }
}
