using System.ComponentModel.DataAnnotations;

namespace PitchGenApi.Model
{
    public class tbl_clientdetails
    {
        [Key]
        public int ClientID { get; set; }  // Primary Key (Identity)
        public string? FirstName { get; set; }
        public string? LastName { get; set; }
        public string? UserName { get; set; }
        public string? Password { get; set; }  // Consider using SecureString for security
        public string? Email { get; set; }
        public string? Position { get; set; }
        public string? CompanyName { get; set; }
        public string? Telephone { get; set; }
        public string? Address { get; set; }
        public DateTime? CreatedOn { get; set; }
        public bool? IsActive { get; set; }
        public string? Ex1 { get; set; }
        public string? Ex2 { get; set; }
        public bool? EmailSendToClient { get; set; }
        public string? Ex4 { get; set; }
        public string? Ex5 { get; set; }
        public string? Ex6 { get; set; }
        public string? Ex7 { get; set; }
        public string?Ex8 { get; set; }
        public string? Ex9 { get; set; }
        public string?Ex10 { get; set; }
        public bool? IsAdmin { get; set; }
        public bool? IsClient { get; set; }
        public int? AccountType { get; set; }
        public string? Otp { get; set; }
        public bool? ShowAnalytics { get; set; }
        public string? AnalyticsLink { get; set; }
        public string? Provider { get; set; }
        public string? ClientStatus { get; set; }
        public int? Credit { get; set; }
        public bool? IsDemoAccount { get; set; }

    }

    public class UpdateDemoAccountDto
    {
        [Required]
        public bool IsDemoAccount { get; set; }
    }
}
