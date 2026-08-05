namespace PitchGenApi.Interfaces
{
    public interface IExtensionRepository
    {
        string GetUnlockedEmail(string linkedInUrl);
        Task<List<string>> GetEmailPatternsAsync(string domain);
        string GenerateEmail(string name, string domain, string emailPattern);
        Task<(bool IsValid, string Stage2Status)> Stage2Async(string email);
        Task UpdateContactEmailAsync(string linkedInUrl, string email, int clientId);
    }
}
