namespace PitchGenApi.Model
{
    public class Subscriptions
    {
        public int Id { get; set; }
        public long? SubscriptionId { get; set; }
        public long? SubscriptionNumber { get; set; }
        public int? ClientId { get; set; }
        public decimal? Amount { get; set; }
        public decimal? SubTotal { get; set; }
        public DateTime? CreatedAt { get; set; }
        public DateTime? ActivatedAt { get; set; }
        public DateTime? CurrentTermStartsAt { get; set; }
        public DateTime? CurrentTermEndsAt { get; set; }
        public DateTime? NextBillingAt { get; set; }
        public long? ProductId { get; set; }
        public string? PlanId { get; set; }
        public long? CustomerId { get; set; }

    }

}
