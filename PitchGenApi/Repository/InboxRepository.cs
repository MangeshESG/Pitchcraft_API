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

    public async Task<List<EmailReplies>> GetRepliesByInboxIdAsync(int inboxId)
    {
        // 🔹 Step 1: Get SMTP (Outbox) Id
        var inbox = await _context.Inboxcredentials
            .Where(x => x.Id == inboxId)
            .Select(x => x.Outboxid)
            .FirstOrDefaultAsync();
        
        var outbox = await _context.SmtpCredentials
            .Where(x => x.Id == inbox)
            .Select(x => x.FromEmail)
            .FirstOrDefaultAsync();

        // 🔹 Step 2: Get sent emails for this SMTP
        var sentEmails = await _context.EmailLogs
            .Where(x => x.SenderEmailId == outbox)
            .Select(x => new { x.MessageId, x.TrackingId, x.ToEmail })
            .ToListAsync();

        var messageIds = sentEmails.Select(x => x.MessageId).ToList();
        var trackingIds = sentEmails.Select(x => x.TrackingId).ToList();
        var toEmails = sentEmails.Select(x => x.ToEmail.ToLower()).ToList();

        // 🔥 FINAL QUERY (ALL FALLBACKS)
        var replies = await _context.EmailReplies
            .Where(er =>
            // ✅ 1. TrackingId (BEST)
            (er.TrackingId != null && trackingIds.Contains(er.TrackingId))
                // ✅ 2. InReplyTo
                || (er.InReplyTo != null && messageIds.Contains(er.InReplyTo))

                // ✅ 3. References
                || (er.InReplyTo == null && er.TrackingId == null
                    && messageIds.Any(mid => er.InReplyTo != null && er.InReplyTo.Contains(mid)))

                // ✅ 4. FromEmail fallback
                || (er.FromEmail != null && toEmails.Contains(er.FromEmail.ToLower()))
            )
            .OrderByDescending(er => er.Date)
            .Distinct()
            .ToListAsync();

        return replies;
    }
    public async Task<List<InboxDropdownDto>> GetInboxPickListByClientIdAsync(int clientId)
    {
        var data = await _context.Inboxcredentials
            .Where(x => x.ClientId == clientId)
            .Select(x => new InboxDropdownDto
            {
                InboxId = x.Id,
                EmailAddress = x.EmailAddress
            })
            .OrderBy(x => x.EmailAddress)
            .ToListAsync();

        return data;
    }
}