namespace PitchGenApi.Model.DTOs
{
    public class ContactFilterDto
    {
        public int ClientId { get; set; }
        public int DataFileId { get; set; }
        public bool IsFollowUp { get; set; }
        public bool NotKrafted { get; set; }
        public bool KraftedNotSent { get; set; }
    }
}
