namespace PitchGenApi.Model.DTOs
{
    public class StripeSubscriptionResponse
    {
        public string Id { get; set; }                // subscription id
        public string CustomerId { get; set; }        // stripe customer id
        public long CurrentPeriodStart { get; set; }  // unix timestamp
        public long CurrentPeriodEnd { get; set; }    // unix timestamp
        public string Status { get; set; }            // active, canceled, etc.
        public string PlanId { get; set; }
    }
}
