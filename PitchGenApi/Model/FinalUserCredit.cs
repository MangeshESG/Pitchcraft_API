namespace PitchGenApi.Model
{
    public class FinalUserCredit
    {
        public int? Id { get; set; }
        public int? ClientId { get; set; }
        public int? MonthlyLimit { get; set; }
        public int? CustomLimit { get; set; }
        public int? CustomCreditUsed { get; set; }
        public int? TotalCredit { get; set; }
        public int? UsedCredit { get; set; }
        public int? LimitUsed { get; set; }
        public DateTime? CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
    }

}
