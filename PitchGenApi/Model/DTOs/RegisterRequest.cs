using Newtonsoft.Json;

namespace PitchGenApi.Model.DTOs
{
    public class RegisterRequest
    {
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public string Email { get; set; }
        public string MobileNumber { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }  // plain password, will hash it
        public string CompanyName { get; set; }
        public string JobTitle { get; set; }
        public string CurrencyCode { get; set; }
        public string Street { get; set; }
        public string City { get; set; }
        public string State { get; set; }
        public string Zip { get; set; }
        public string Country { get; set; }
        public string StateCode { get; set; }
    }
}

