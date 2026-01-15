namespace PitchGenApi.Model.DTOs
{
    public class DomainVeryficationStatus
    {
        public string Domain { get; set; }
        public int Domainid { get; set; }
        public int EmailDomainId { get; set; }
        public bool EmailDomainverified { get; set; }
        public bool Domainverified { get; set; }
        public string token { get; set; }
        public string Dmark { get; set; }
    }
}
