namespace PitchGenApi.DTOs
{
    public class ContactDto
    {
        public string? fullName { get; set; }
        public string? email { get; set; }
        public string? website { get; set; }
        public string? companyName { get; set; }
        public string? jobTitle { get; set; }
        public string? linkedInUrl { get; set; }
        public string? countryOrAddress { get; set; }
        public string? emailSubject { get; set; }
        public string? emailBody { get; set; }
        public string? CompanyTelephone { get; set; }
        public string? CompanyEmployeeCount { get; set; }
        public string? CompanyIndustry { get; set; }
        public string? CompanyLinkedInURL { get; set; }
        //public string? CompanyEventLink { get; set; }
        public string? linkedIninformation { get; set; }
        public int clientId { get; set; }
        public string? firstName { get; set; }
        public string? lastName { get; set; }
        public Dictionary<string, string>? customFields { get; set; }
    }
}
