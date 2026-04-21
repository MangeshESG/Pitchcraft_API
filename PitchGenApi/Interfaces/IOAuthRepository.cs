namespace PitchGenApi.Interfaces
{
    public interface IOAuthRepository
    {
        Task<string> GmailGetAuthUrlAsync(int clientId, string SenderName);
        Task<string> GmailHandleCallbackAsync(string code,int clientId, string SenderName);
    }
}
