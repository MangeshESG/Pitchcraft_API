namespace PitchGenApi.Model.DTOs
{
    public class UnlockEmailResult
    {
        public string? ContactID { get; init; }
        public bool Success { get; init; }
        public string Email { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;

        public static UnlockEmailResult Succeeded(string? contactId, string email, string status) =>
            new() { ContactID = contactId, Success = true, Email = email, Status = status };

        public static UnlockEmailResult Failed(string? contactId, string status) =>
            new() { ContactID = contactId, Success = false, Status = status };
    }
}
