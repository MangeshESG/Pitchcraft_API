namespace PitchGenApi.Interfaces
{
    using PitchGenApi.Model.DTOs;

    public interface IExtensionRepository
    {
        Task<ExtensionOperationResult> MatchContactAsync(ContactMatchRequestDto request);
        Task<ExtensionOperationResult> AddContactToDataFileAsync(AddContactToDataFileRequestDto request);
        Task<ExtensionOperationResult> UpdateContactFieldsAsync(UpdateContactFieldsRequestDto request);
        Task<string?> GetUnlockedEmailAsync(string domain, string? linkedInUrl);
        Task<string?> GetProspeoUnlockedEmailAsync(string linkedInUrl);
        Task<List<string>> GetEmailPatternsAsync(string domain);
        IReadOnlyList<string> GetAllEmailPatterns();
        string GenerateEmail(string name, string domain, string emailPattern);
        Task<EmailVerificationResult> Stage2Async(
            string email,
            CancellationToken cancellationToken = default);
        Task<EmailVerificationResult> Stage3Async(
            string email,
            string firstName,
            string? contactId,
            int clientId,
            CancellationToken cancellationToken = default);
        Task<bool> CompleteUnlockAsync(
            string? contactId,
            int clientId,
            string? linkedInUrl,
            string email,
            string name,
            string domain);
        Task<bool> CompleteProspeoUnlockAsync(
            string? contactId,
            int clientId,
            string linkedInUrl,
            string email);
    }
}
