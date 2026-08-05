namespace PitchGenApi.Model.DTOs
{
    public class GetUnlockedEmailRequest
    {
        public string Name { get; set; }
        public string CompanyName { get; set; }
        public string Domain { get; set; }
        public string ContactID { get; set; }
        public int ClientID { get; set; }
        public string LinkedInUrl { get; set; }
    }
}
