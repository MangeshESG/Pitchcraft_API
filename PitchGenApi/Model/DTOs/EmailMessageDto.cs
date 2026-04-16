namespace PitchGenApi.Model.DTOs
{
    public class EmailMessageDto
    {
        public string Subject { get; set; }
        public string From { get; set; }
        public string Body { get; set; }
        public DateTime Date { get; set; }
        public string MessageId { get; set; }
    }
}
