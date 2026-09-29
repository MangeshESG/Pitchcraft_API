namespace PitchGenApi.Interfaces
{
    public interface IUnsubscribeRepository
    {
        Task<string> GenerateUnsubscribeLinkAsync(string companyName, int clientId, int contactId, string email);
        Task<string> GenerateOneClickUnsubscribeLinkAsync(string companyName, int clientId, int contactId, string email);
    }
}
