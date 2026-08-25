namespace PitchGenApi.Model.DTOs
{
    public class UnlockEmailResult
    {
        public string? ContactID { get; init; }
        public bool Success { get; init; }
        public string Email { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public string Source { get; init; } = string.Empty;

        /// <summary>
        /// A trace of the unlock, attached only when the caller is an admin.
        /// Null for everyone else - it carries the raw prompt and raw model
        /// output, so it must never reach a normal client.
        /// </summary>
        public UnlockDiagnostics? Diagnostics { get; set; }

        public static UnlockEmailResult Succeeded(
            string? contactId,
            string email,
            string status,
            string source = "prospeo") =>
            new()
            {
                ContactID = contactId,
                Success = true,
                Email = email,
                Status = status,
                Source = source
            };

        public static UnlockEmailResult Failed(string? contactId, string status) =>
            new() { ContactID = contactId, Success = false, Status = status };
    }
}
