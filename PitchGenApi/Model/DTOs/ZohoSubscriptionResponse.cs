using Newtonsoft.Json;

namespace PitchGenApi.Model.DTOs
{
    public class ZohoSubscriptionResponse
    {
        [JsonProperty("subscription")]
        public ZohoSubscription Subscription { get; set; }
    }

    public class ZohoSubscription
    {
        [JsonProperty("subscription_id")]
        public string SubscriptionId { get; set; }

        [JsonProperty("subscription_number")]
        public string SubscriptionNumber { get; set; }

        [JsonProperty("customer_id")]
        public string CustomerId { get; set; }

        [JsonProperty("plan")]
        public ZohoPlan Plan { get; set; }

        [JsonProperty("product_id")]
        public string ProductId { get; set; }

        [JsonProperty("created_time")]
        public DateTime? CreatedAt { get; set; }

        [JsonProperty("activated_date")]
        public DateTime? ActivatedAt { get; set; }

        [JsonProperty("current_term_start")]
        public DateTime? CurrentTermStartsAt { get; set; }

        [JsonProperty("current_term_end")]
        public DateTime? CurrentTermEndsAt { get; set; }

        [JsonProperty("next_billing_at")]
        public DateTime? NextBillingAt { get; set; }

        [JsonProperty("total")]
        public decimal? Amount { get; set; }

        [JsonProperty("subtotal")]
        public decimal? SubTotal { get; set; }

        // Add more fields if needed
    }

    public class ZohoPlan
    {
        [JsonProperty("plan_code")]
        public string PlanId { get; set; }
    }

}
