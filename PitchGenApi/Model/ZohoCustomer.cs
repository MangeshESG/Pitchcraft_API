namespace PitchGenApi.Model
{
    public class ZohoCustomer
    {
        public int Id { get; set; }  // Auto-incremented primary key
        public int ClientId { get; set; }

        public string CustomerId { get; set; }          // Zoho Customer ID
        public string ContactId { get; set; }
        public string PrimaryContactPersonId { get; set; }

        public string DisplayName { get; set; }
        public string ContactName { get; set; }
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public string Email { get; set; }
        public string Mobile { get; set; }
        public string Status { get; set; }
        public DateTime? Createdat { get; set; }
    }
}
