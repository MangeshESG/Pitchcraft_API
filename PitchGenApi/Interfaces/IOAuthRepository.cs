namespace PitchGenApi.Interfaces
{
    public interface IOAuthRepository
    {
        Task<string> GmailGetAuthUrlAsync(int clientId, string SenderName, bool FullInboxSync);
        Task<string> GmailHandleCallbackAsync(string code, int clientId, string SenderName, bool FullInboxSync);
        Task<string> OutlookGetAuthUrlAsync(int clientId, string senderName, bool FullInboxSync);
        Task<string> OutlookHandleCallbackAsync(string code, int clientId, string SenderName, bool FullInboxSync);
    }
}
