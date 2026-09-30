namespace PitchGenApi.Model
{
    public class UnsubscribeTokens
    {
        public long Id { get; set; }

        public int? ClientId { get; set; }

        public int ContactId { get; set; }

        public string? Email { get; set; }

        public string? CompanyName { get; set; }

        public string? CompanySlug { get; set; }

        public string? Token { get; set; }

        public DateTime? CreatedAt { get; set; }

        public DateTime? UpdatedAt { get; set; }

        public bool? IsActive { get; set; }
    }
}
