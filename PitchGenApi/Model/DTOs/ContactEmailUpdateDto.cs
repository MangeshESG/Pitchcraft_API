namespace PitchGenApi.Model.DTOs
{
    public class ContactEmailUpdateDto
    {  
        public int ClientId { get; set; }
        public int ContactId { get; set; }
        public bool? GPTGenerate { get; set; }
        public string? EmailSubject { get; set; }
        public string? EmailBody { get; set; }
    }
}
