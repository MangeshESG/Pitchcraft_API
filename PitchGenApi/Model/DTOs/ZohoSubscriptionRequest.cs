namespace PitchGenApi.Model.DTOs
{
    using Newtonsoft.Json;

    public class ZohoSubscriptionRequest
    {
        [JsonProperty("customer_id")]
        public string CustomerId { get; set; }

        [JsonProperty("plan")]
        public Plan Plan { get; set; }

        [JsonProperty("auto_collect")]
        public bool AutoCollect { get; set; } = false;
    }

    public class Plan
    {
        [JsonProperty("plan_code")]
        public string PlanCode { get; set; }

        [JsonProperty("quantity")]
        public int Quantity { get; set; }
    }

}
