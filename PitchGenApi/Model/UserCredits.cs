namespace PitchGenApi.Model
{
    public class UserCredits
    {
        public int? Id { get; set; }
        public int? ClientId { get; set; }
        public int? Credits { get; set; }
        public int? MonthlyLimit { get; set; }
        public int? TotalPurchesdCredit { get; set; }
        public string? Status { get; set; }
        public decimal? Amount { get; set; }
        public string? Plane { get; set; }
        public string? Interval { get; set; }
        public string? StripeSubscriptionId { get; set; }
        public string? SubscriptionNumber { get; set; }
        public DateTime ResetDate { get; set; }
        public DateTime? CreatedAt { get; set; }
        public DateTime? StartDate { get; set; }
        public DateTime? EndDate { get; set; }
    }
}
