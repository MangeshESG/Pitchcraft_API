namespace PitchGenApi.Model.DTOs
{
    public class SendEmailRequestDto
    {
        public int clientId { get; set; }
        public int contactid { get; set; }
        public int? DataFileId { get; set; }
        public int? SegmentId { get; set; }
        public string ToEmail { get; set; }
        public string Subject { get; set; }
        public bool isFollowUp { get; set; }
        public string BccEmail { get; set; }
        public int SmtpId { get; set; }

        public string FullName { get; set; }
        public string CountryOrAddress { get; set; }
        public string CompanyName { get; set; }
        public string Website { get; set; }
        public string LinkedinUrl { get; set; }
        public string JobTitle { get; set; }
    }

}
