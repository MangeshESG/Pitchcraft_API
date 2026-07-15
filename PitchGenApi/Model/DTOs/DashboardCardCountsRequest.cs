using System;

namespace PitchGenApi.Model.DTOs
{
    public class DashboardCardCountsRequest
    {
        public int ClientId { get; set; }
        public int? CampaignId { get; set; }
        public DateTime? StartDate { get; set; }
        public DateTime? EndDate { get; set; }
        public int? OutboxId { get; set; }
        public bool ExcludeBots { get; set; }
    }
}
