using MailKit.Net.Imap;
using MailKit.Security;
using PitchGenApi.Interfaces;
using PitchGenApi.Model;
using PitchGenApi.Model.DTOs;

public class InboxEmailService : IInboxEmailService
{
    public async Task<bool> TestConnectionAsync(Inboxcredentials s)
    {
        try
        {
            using var client = new ImapClient();
            await client.ConnectAsync(s.Host, s.Port, s.UseSSL);
            await client.AuthenticateAsync(s.Username, s.Password);
            await client.DisconnectAsync(true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<List<EmailMessageDto>> GetEmailsAsync(Inboxcredentials s)
    {
        var result = new List<EmailMessageDto>();

        using var client = new ImapClient();
        await client.ConnectAsync(s.Host, s.Port, s.UseSSL);
        await client.AuthenticateAsync(s.Username, s.Password);

        var inbox = client.Inbox;
        await inbox.OpenAsync(MailKit.FolderAccess.ReadOnly);

        for (int i = inbox.Count - 10; i < inbox.Count; i++)
        {
            if (i < 0) continue;

            var msg = await inbox.GetMessageAsync(i);

            result.Add(new EmailMessageDto
            {
                Subject = msg.Subject,
                From = msg.From.ToString(),
                Body = msg.TextBody,
                Date = msg.Date.DateTime,
                MessageId = msg.MessageId
            });
        }

        await client.DisconnectAsync(true);
        return result;
    }
}