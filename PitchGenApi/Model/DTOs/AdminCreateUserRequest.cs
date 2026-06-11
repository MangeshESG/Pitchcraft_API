namespace PitchGenApi.Model.DTOs
{
    public class AdminCreateUserRequest
    {
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public string Email { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
        public string CompanyName { get; set; }
        public string JobTitle { get; set; }
    }
}
