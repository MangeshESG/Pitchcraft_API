namespace PitchGenApi.Model.DTOs
{
    public class CreateSubscriptionRequest
    {
        public string UserId { get; set; } = "";
        public string PriceId { get; set; } = ""; // price_xxx from Stripe
        public string? Email { get; set; }
    }


}
