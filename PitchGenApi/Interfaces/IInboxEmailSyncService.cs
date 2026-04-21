using PitchGenApi.Model;

namespace PitchGenApi.Interfaces
{
    public interface IInboxEmailSyncService
    {
        Task SyncEmailsAsync(Inboxcredentials setting);
        Task SyncGmailInboxAsync(EmailOAuthTokens tokenData);
    }
}
