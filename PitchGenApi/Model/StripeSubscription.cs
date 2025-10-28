namespace PitchGenApi.Model
{
    public class StripeSubscription
    {
        public int Id { get; set; }                      // Primary key
        public string UserId { get; set; }                 // App ke user ka unique id
        public string StripeCustomerId { get; set; } = "";   // Stripe customer id
        public string StripeSubscriptionId { get; set; } = ""; // Stripe subscription id
        public string PlanId { get; set; } = "";         // Stripe price id / plan id
        public DateTime StartDate { get; set; }          // Payment start date
        public DateTime? EndDate { get; set; }           // Optional (if subscription ends)
        public string Status { get; set; } = "Active";
    }
}
