using Microsoft.EntityFrameworkCore;
using PitchGenApi.Database;
using PitchGenApi.Interfaces;

namespace PitchGenApi.Repository
{
    public class InboxRefreshJob : IInboxRefreshJob
    {
        private readonly IServiceScopeFactory _scopeFactory;

        public InboxRefreshJob(IServiceScopeFactory scopeFactory)
        {
            _scopeFactory = scopeFactory;
        }

        public async Task<string> RunSelectedAsync(int inboxId, string provider)
        {
            using var scope = _scopeFactory.CreateScope();

            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var inboxService = scope.ServiceProvider.GetRequiredService<IInboxEmailSyncService>();

            provider = provider.ToUpper();

            switch (provider)
            {
                case "IMAP":
                case "SMTP":
                    {
                        var imap = await context.Inboxcredentials
                            .FirstOrDefaultAsync(x => x.Id == inboxId);

                        if (imap == null)
                            return "Inbox credential not found";

                        await inboxService.SyncEmailsAsync(imap);

                        return $"inbox refreshed successfully";
                    }

                case "GMAIL":
                    {
                        var oauth = await context.EmailOAuthTokens
                            .FirstOrDefaultAsync(x =>
                                x.Id == inboxId &&
                                x.Provider.ToUpper() == "GMAIL");

                        if (oauth == null)
                            return "Gmail token not found";

                        await inboxService.SyncGmailInboxAsync(oauth);

                        return "inbox refreshed successfully";
                    }

                case "OUTLOOK":
                    {
                        var oauth = await context.EmailOAuthTokens
                            .FirstOrDefaultAsync(x =>
                                x.Id == inboxId &&
                                x.Provider.ToUpper() == "OUTLOOK");

                        if (oauth == null)
                            return "Outlook token not found";

                        await inboxService.SyncOutlookInboxAsync(oauth);

                        return "inbox refreshed successfully";
                    }

                default:
                    return "Invalid provider";
            }
        }

        public async Task RunOtherClientInboxesAsync(int clientId, int selectedInboxId, string selectedProvider)
        {
            using var scope = _scopeFactory.CreateScope();

            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            selectedProvider = selectedProvider.ToUpper();

            var imapInboxes = await context.Inboxcredentials
                .Where(x => x.ClientId == clientId)
                .Select(x => new
                {
                    x.Id,
                    Provider = "IMAP"
                })
                .ToListAsync();

            var oauthInboxes = await context.EmailOAuthTokens
                .Where(x => x.ClientId == clientId)
                .Select(x => new
                {
                    x.Id,
                    Provider = x.Provider.ToUpper()
                })
                .ToListAsync();

            var allInboxes = imapInboxes
                .Concat(oauthInboxes)
                .Where(x =>
                    !(x.Id == selectedInboxId &&
                      x.Provider.ToUpper() == selectedProvider))
                .ToList();

            Console.WriteLine($"🔄 Background refresh inbox count: {allInboxes.Count}");

            foreach (var inbox in allInboxes)
            {
                try
                {
                    await RunSelectedAsync(inbox.Id, inbox.Provider);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ Background inbox refresh failed. InboxId={inbox.Id}, Provider={inbox.Provider}, Error={ex.Message}");
                }
            }
        }
    }
}