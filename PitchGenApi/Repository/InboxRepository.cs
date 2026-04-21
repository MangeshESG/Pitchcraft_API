using MailKit.Net.Imap;
using MailKit.Net.Pop3;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using PitchGenApi.Database;
using PitchGenApi.Interfaces;
using PitchGenApi.Model;
using PitchGenApi.Model.DTOs;

public class InboxRepository : IInboxRepository
{
    private readonly AppDbContext _context;

    public InboxRepository(AppDbContext context)
    {
        _context = context;
    }

    public async Task AddAsync(Inboxcredentials setting)
    {
        await _context.Inboxcredentials.AddAsync(setting);
        await _context.SaveChangesAsync();
    }

    public async Task DeleteAsync(int id)
    {
        var setting = await _context.Inboxcredentials.FindAsync(id);
        if (setting != null)
        {
            _context.Inboxcredentials.Remove(setting);
            await _context.SaveChangesAsync();
        }
    }

    public async Task<IEnumerable<Inboxcredentials>> GetAllAsync()
    {
        return await _context.Inboxcredentials.ToListAsync();
    }

    public async Task<Inboxcredentials?> GetByIdAsync(int id)
    {
        return await _context.Inboxcredentials.FindAsync(id);
    }

    public async Task<List<Inboxcredentials>> GetByUserIdAsync(int clientId)
    {
        return await _context.Inboxcredentials
                             .Where(x => x.ClientId == clientId)
                             .ToListAsync();
    }
    
    public async Task<Inboxcredentials?> GetByUserNameAsync(int userId, string username, string protocol)
    {
        return await _context.Inboxcredentials
                             .FirstOrDefaultAsync(x => x.ClientId == userId && x. Username == username && x.Protocol == protocol);
    }
    public async Task<bool> ValidateAsync(InboxcredentialsDTO dto)
    {
        try
        {
            if (dto.Protocol.ToUpper() == "IMAP")
            {
                using var client = new ImapClient();

                await client.ConnectAsync(dto.Host, dto.Port, dto.UseSSL ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls);

                await client.AuthenticateAsync(dto.Username, dto.Password);

                await client.DisconnectAsync(true);

                return true;
            }
            else if (dto.Protocol.ToUpper() == "POP3")
            {
                using var client = new Pop3Client();

                await client.ConnectAsync(dto.Host, dto.Port, dto.UseSSL ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls);

                await client.AuthenticateAsync(dto.Username, dto.Password);

                await client.DisconnectAsync(true);

                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }
    public async Task UpdateAsync(Inboxcredentials setting)
    {
        _context.Inboxcredentials.Update(setting);
        await _context.SaveChangesAsync();
    }

    public async Task<List<EmailReplies>> GetRepliesByInboxIdAsync(int inboxId, string Provider)
    {
        try
        {
            int? outboxId = 0;

            // =========================
            // 🔥 IMAP FLOW
            // =========================
            if (Provider.ToUpper() == "IMAP")
            {
                outboxId = await _context.Inboxcredentials
                    .Where(x => x.Id == inboxId)
                    .Select(x => x.Outboxid)
                    .FirstOrDefaultAsync();

                if (outboxId == 0)
                    return new List<EmailReplies>();
            }

            // =========================
            // 🔥 GMAIL FLOW
            // =========================
            else if (Provider.ToUpper() == "GMAIL")
            {
                // 🔥 Gmail me InboxId = OutboxId (same id use kar rahe ho)
                outboxId = inboxId;
            }

            if (outboxId == 0)
                return new List<EmailReplies>();

            // =========================
            // 🔥 STEP 2: SENT EMAILS (OutboxId based)
            // =========================
            var sentEmails = await _context.EmailLogs
                .Where(x => x.outboxid == outboxId)   // ✅ MAIN FIX
                .Select(x => new
                {
                    x.MessageId,
                    x.TrackingId,
                    ToEmail = x.ToEmail ?? ""
                })
                .ToListAsync();

            var messageIds = sentEmails
                .Where(x => !string.IsNullOrEmpty(x.MessageId))
                .Select(x => x.MessageId)
                .ToList();

            var trackingIds = sentEmails
                .Where(x => x.TrackingId != null)
                .Select(x => x.TrackingId)
                .ToList();

            var toEmails = sentEmails
                .Where(x => !string.IsNullOrEmpty(x.ToEmail))
                .Select(x => x.ToEmail.ToLower())
                .ToList();

            // =========================
            // 🔥 FINAL QUERY (Replies only)
            // =========================
            var replies = await _context.EmailReplies
                .Where(er =>
                    (er.TrackingId != null && trackingIds.Contains(er.TrackingId))
                    || (er.InReplyTo != null && messageIds.Contains(er.InReplyTo))
                    //|| (er.FromEmail != null && toEmails.Contains(er.FromEmail.ToLower()))
                )
                .OrderByDescending(er => er.Date)
                .ToListAsync();

            return replies;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error: {ex.Message}");

            if (ex.InnerException != null)
                Console.WriteLine($"🔍 Inner: {ex.InnerException.Message}");

            return new List<EmailReplies>();
        }
    }
    public async Task<List<InboxDropdownDto>> GetInboxPickListByClientIdAsync(int clientId)
    {
        try
        {
            // 🔥 IMAP
            var inboxData = await _context.Inboxcredentials
                .Where(x => x.ClientId == clientId)
                .Select(x => new InboxDropdownDto
                {
                    InboxId = x.Id,
                    EmailAddress = x.EmailAddress ?? "",
                    Provider = "IMAP"
                })
                .ToListAsync();

            // 🔥 OAuth (ALL providers)
            var oauthData = await _context.EmailOAuthTokens
                .Where(x => x.ClientId == clientId)
                .Select(x => new InboxDropdownDto
                {
                    InboxId = x.Id,
                    EmailAddress = x.Email ?? "",
                    Provider = x.Provider ?? "Unknown"
                })
                .ToListAsync();

            // 🔥 Merge + Remove duplicates
            var result = inboxData
                .Concat(oauthData)
                .Where(x => !string.IsNullOrEmpty(x.EmailAddress))
                .GroupBy(x => x.EmailAddress.ToLower())
                .Select(g => g.First())
                .OrderBy(x => x.EmailAddress)
                .ToList();

            return result;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error in GetInboxPickListByClientIdAsync: {ex.Message}");
            return new List<InboxDropdownDto>();
        }
    }
}