namespace PitchGenApi.Model
{
    public class EmailRequest
    {
        public decimal Cost { get; set; }
        public int SuccessReq { get; set; }
        public int userid { get; set; }
        public string Role { get; set; }
        public string lastPitch { get; set; }
        public int TotalTokensUsed { get; set; }
        public string TimeSpent { get; set; } = string.Empty;
        public DateTime? StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public List<object> GeneratedPitches { get; set; } = new();
        public string PromptText { get; set; } = "No prompt template was selected";
        public bool IsPauseReport { get; set; }
    }

}
