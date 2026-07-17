namespace PitchGenApi.Model.DTOs
{
    public class SendEmailRequestDto
    {
        public int clientId { get; set; }
        public int contactid { get; set; }
        public int? campaignid { get; set; }
        public bool isFollowUp { get; set; }
        public List<string>? CcEmail { get; set; }
        public List<string>? BccEmail { get; set; }
        public int Outboxid { get; set; }
        public int SegmentId { get; set; }
        public string Type { get; set; }
        public Guid trackingid { get; set; }
    }

}


