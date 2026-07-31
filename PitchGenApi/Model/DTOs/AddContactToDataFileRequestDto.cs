namespace PitchGenApi.Model.DTOs
{
    public class AddContactToDataFileRequestDto
    {
        public int ClientId { get; set; }
        public int DataFileId { get; set; }
        public string? ContactName { get; set; }
        public string? JobTitle { get; set; }
        public string? LinkedInUrl { get; set; }
        public string? CompanyName { get; set; }
        public string? Location { get; set; }
    }
}
