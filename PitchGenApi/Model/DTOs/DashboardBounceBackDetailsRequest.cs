using System;

namespace PitchGenApi.Model.DTOs
{
    public class DashboardBounceBackDetailsRequest
    {
        public int ClientId { get; set; }
        public int? CampaignId { get; set; }
        public DateTime? StartDate { get; set; }
        public DateTime? EndDate { get; set; }
        public int? OutboxId { get; set; }
        public int PageNumber { get; set; } = 1;
        public int PageSize { get; set; } = 10;
        public string? Search { get; set; }
    }
}
