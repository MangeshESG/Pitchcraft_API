using PitchGenApi.Models;

namespace PitchGenApi.Model.DTOs
{
    public class FullTrackingDataResponse
    {
        public List<Contact> Contacts { get; set; }
        public List<EmailTrackingLog> EmailTrackingLogs { get; set; }
        public List<EmailLog> EmailLogs { get; set; }
    }
}
