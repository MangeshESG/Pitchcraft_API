namespace PitchGenApi.Model.DTOs
{
    // Profile details returned to the profile page.
    public class UserProfileDto
    {
        public int Id { get; set; }
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public string Email { get; set; }
        public string Username { get; set; }
        public string CompanyName { get; set; }
        public string JobTitle { get; set; }
        public bool IsAdmin { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    // Editable profile fields. Email and Username are both sign-in identifiers,
    // so the controller checks they stay unique across ClientDetails.
    public class UpdateProfileRequest
    {
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public string Email { get; set; }
        public string Username { get; set; }
        public string CompanyName { get; set; }
        public string JobTitle { get; set; }
    }

    // Password change from the profile page: old password verified, then replaced.
    public class ChangePasswordRequest
    {
        public string CurrentPassword { get; set; }
        public string NewPassword { get; set; }
    }
}
