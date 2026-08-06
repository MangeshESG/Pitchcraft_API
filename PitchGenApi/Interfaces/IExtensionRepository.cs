namespace PitchGenApi.Interfaces
{
    using PitchGenApi.Model.DTOs;

    public interface IExtensionRepository
    {
        Task<string?> GetUnlockedEmailAsync(string contactId, int clientId, string? linkedInUrl);
        Task<List<string>> GetEmailPatternsAsync(string domain);
        IReadOnlyList<string> GetAllEmailPatterns();
        string GenerateEmail(string name, string domain, string emailPattern);
        Task<EmailVerificationResult> Stage2Async(
            string email,
            CancellationToken cancellationToken = default);
        Task<EmailVerificationResult> Stage3Async(
            string email,
            string firstName,
            string contactId,
            int clientId,
            CancellationToken cancellationToken = default);
        Task<bool> CompleteUnlockAsync(
            string contactId,
            int clientId,
            string? linkedInUrl,
            string email,
            string name,
            string domain);
    }
}
