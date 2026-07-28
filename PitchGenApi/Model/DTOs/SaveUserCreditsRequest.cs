namespace PitchGenApi.Model.DTOs
{
    public class SaveUserCreditsRequest
    {
        public int UserId { get; set; }

        public string PlanId { get; set; } = string.Empty;

        public int? CreditsCount { get; set; }
    }
}
