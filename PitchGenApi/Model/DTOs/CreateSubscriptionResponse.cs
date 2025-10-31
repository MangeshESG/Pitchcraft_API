namespace PitchGenApi.Model.DTOs
{
    public class CreateSubscriptionResponse
    {
        public string SubscriptionNumber { get; set; } = string.Empty;
        public string SubscriptionId { get; set; } = string.Empty;
        public string? ClientSecret { get; set; }
    }
}