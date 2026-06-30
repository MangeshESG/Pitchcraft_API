namespace PitchGenApi.Interfaces
{
    public interface IInboxRefreshJob
    {
        Task<string> RunSelectedAsync(int inboxId, string provider);
        Task RunOtherClientInboxesAsync(int clientId, int selectedInboxId, string selectedProvider);
    }
}
