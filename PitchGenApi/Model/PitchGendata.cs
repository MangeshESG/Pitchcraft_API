namespace PitchGenApi.Model
{
    public class PitchGendata
    {
        public int Id { get; set; }
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public string EmailAddress { get; set; }
        public string JobTitle { get; set; }
        public string CompanyName { get; set; }
        public string CompanyIndustrySector { get; set; }
        public string Location { get; set; }
        public string JobFunction { get; set; }
        public string JobLevel { get; set; } // Note: Removed extra space
    }
}