namespace PitchGenApi.Model.DTOs
{
    public class StripeSubscriptionResponse
    {
        public List<object> Data { get; set; } = new List<object>();
        public bool HasMore { get; set; }
        public string? NextCursor { get; set; }
    }
}