using MailKit.Net.Imap;
using MailKit.Net.Pop3;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using PitchGenApi.Database;
using PitchGenApi.Interfaces;
using PitchGenApi.Model;
using PitchGenApi.Model.DTOs;
using System.Text;

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

                // map UI value to SecureSocketOptions
                var option = GetSecureOption(dto.encryption);

                await client.ConnectAsync(dto.Host, dto.Port, option);

                await client.AuthenticateAsync(dto.Username, dto.Password);
                await client.DisconnectAsync(true);

                return true;
            }
            else if (dto.Protocol.ToUpper() == "POP3")
            {
                using var client = new Pop3Client();

                var option = GetSecureOption(dto.encryption);

                await client.ConnectAsync(dto.Host, dto.Port, option);

                await client.AuthenticateAsync(dto.Username, dto.Password);
                await client.DisconnectAsync(true);

                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Mail validation error: " + ex.Message);
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
            else if (Provider.ToUpper() == "GMAIL" || Provider.ToUpper() == "OUTLOOK")
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

    public async Task<bool> MarkEmailAsReadAsync(string replyId)
    {
        var email = await _context.EmailReplies
            .FirstOrDefaultAsync(x => x.MessageId == replyId);

        if (email == null)
            return false;

        email.IsRead = true;

        await _context.SaveChangesAsync();
        return true;
    }

    public async Task<string> BuildEmailThreadForInbox(int clientId, Guid trackingId)
    {
        // 🔥 SENT MAILS
        var sentMails = await _context.EmailLogs
            .Where(x => x.ClientId == clientId
                        && x.TrackingId == trackingId
                        && x.IsSuccess == true)
            .Select(x => new
            {
                Type = "Sent",
                x.MessageId,
                x.Subject,
                x.Body,
                x.SentAt,
                From = x.SenderEmailId,
                FromName = x.EmailSenderName,
                To = x.ToEmail,
                ToName = x.EmailRecipientName
            })
            .ToListAsync();

        // 🔥 REPLIES
        var replies = await _context.EmailReplies
            .Where(x => x.ClientId == clientId
                        && x.TrackingId == trackingId)
            .Select(x => new
            {
                Type = "Reply",
                x.MessageId,
                x.Subject,
                Body = x.Body,
                SentAt = x.Date,
                From = x.FromEmail,
                FromName = x.FromEmail,
                To = "",   // optional
                ToName = ""
            })
            .ToListAsync();

        // 🔥 MERGE
        var allMails = sentMails.Concat(replies)
            .OrderBy(x => x.SentAt)
            .ToList();

        if (!allMails.Any())
            return "";

        // 🔥 BUILD HTML THREAD
        StringBuilder sb = new StringBuilder();

        foreach (var mail in allMails)
        {
            sb.AppendLine("<hr style='border:0; border-top:0.5px solid #999; width:100%;' />");

            sb.AppendLine($"<b>From:</b> {mail.FromName} &lt;{mail.From}&gt;<br/>");
            sb.AppendLine($"<b>Sent:</b> {mail.SentAt:dddd, MMMM d, yyyy h:mm tt}<br/>");

            if (!string.IsNullOrEmpty(mail.To))
                sb.AppendLine($"<b>To:</b> {mail.ToName} &lt;{mail.To}&gt;<br/>");

            sb.AppendLine($"<b>Subject:</b> {mail.Subject}<br/><br/>");

            sb.AppendLine($"{mail.Body}<br/><br/>");
        }

        return sb.ToString();
    }
    public async Task<List<EmailThreadDto>> GetInboxThreads(int inboxId, string Provider)
    {
        int? outboxId = 0;

        // =========================
        // OUTBOX RESOLVE
        // =========================
        if (Provider.ToUpper() == "IMAP")
        {
            outboxId = await _context.Inboxcredentials
                .Where(x => x.Id == inboxId)
                .Select(x => x.Outboxid)
                .FirstOrDefaultAsync();
        }
        else if (Provider.ToUpper() == "GMAIL" || Provider.ToUpper() == "OUTLOOK")
        {
            outboxId = inboxId;
        }

        if (outboxId == 0)
            return new List<EmailThreadDto>();

        // =========================
        // SENT EMAILS
        // =========================
        var sentEmails = await _context.EmailLogs
            .Where(x => x.outboxid == outboxId && x.IsSuccess)
            .ToListAsync();

        var messageIds = sentEmails
            .Where(x => !string.IsNullOrEmpty(x.MessageId))
            .Select(x => x.MessageId)
            .ToList();

        var trackingIds = sentEmails
            .Where(x => x.TrackingId != null)
            .Select(x => x.TrackingId)
            .ToList();

        // =========================
        // REPLIES
        // =========================
        var replies = await _context.EmailReplies
            .Where(er =>
                (er.TrackingId != null && trackingIds.Contains(er.TrackingId))
                || (er.InReplyTo != null && messageIds.Contains(er.InReplyTo))
            )
            .ToListAsync();

        // =========================
        // CONTACT MAP
        // =========================
        var contactIds = sentEmails
            .Where(x => x.ContactId != null)
            .Select(x => x.ContactId.Value)
            .Union(
                replies.Where(x => x.ContactId != null)
                       .Select(x => x.ContactId.Value)
            )
            .Distinct()
            .ToList();

        var contactMap = await _context.contacts
            .Where(x => contactIds.Contains(x.id))
            .ToDictionaryAsync(x => x.id, x => x.full_name);

        // =========================
        // THREAD BUILD
        // =========================
        var threads = sentEmails
            .GroupBy(x => x.TrackingId)
            .Select(g =>
            {
                var threadMessages = new List<EmailConvDto>();

                var groupContactId = g.FirstOrDefault(x => x.ContactId != null)?.ContactId ?? 0;

                var groupContactName = contactMap.ContainsKey(groupContactId)
                    ? contactMap[groupContactId]
                    : "";

                // SENT
                threadMessages.AddRange(g.Select(s => new EmailConvDto
                {
                    Type = "Sent",
                    MessageId = s.MessageId,
                    Subject = s.Subject,
                    Body = s.Body,
                    FromEmail = s.SenderEmailId,
                    ToEmail = s.ToEmail,
                    Date = s.SentAt,
                    IsRead = true,
                    ContactId = s.ContactId ?? groupContactId,
                    ContactName =  s.EmailSenderName
                }));

                // REPLY
                threadMessages.AddRange(
                    replies
                    .Where(r =>
                        r.TrackingId == g.Key ||
                        g.Select(x => x.MessageId).Contains(r.InReplyTo))
                    .Select(r => new EmailConvDto
                    {
                        Type = "Reply",
                        MessageId = r.MessageId,
                        Subject = r.Subject,
                        Body = r.Body,
                        FromEmail = r.FromEmail,
                        ToEmail = g.FirstOrDefault()?.SenderEmailId ?? "",
                        Date = r.Date,
                        IsRead = r.IsRead ?? false,
                        ContactId = r.ContactId ?? groupContactId,
                        ContactName = contactMap.ContainsKey(r.ContactId ?? groupContactId)
                            ? contactMap[r.ContactId ?? groupContactId]
                            : r.FromEmail
                    })
                );

                threadMessages = threadMessages
                    .OrderBy(x => x.Date)
                    .ToList();

                return new EmailThreadDto
                {
                    TrackingId = g.Key,
                    Subject = g.FirstOrDefault()?.Subject,
                    ContactEmail = g.FirstOrDefault()?.ToEmail,
                    TotalMessages = threadMessages.Count,
                    LastMessageDate = threadMessages.Max(x => x.Date),
                    HasUnread = threadMessages.Any(x => x.Type == "Reply" && !x.IsRead),
                    ContactId = groupContactId,
                    Messages = threadMessages
                };
            })
            .OrderByDescending(x => x.LastMessageDate)
            .ToList();

        return threads;
    }
    public SecureSocketOptions GetSecureOption(string encryption)
    {
        return encryption?.ToUpper() switch
        {
            "SSL/TLS" => SecureSocketOptions.SslOnConnect,
            "STARTTLS" => SecureSocketOptions.StartTls,
            "NONE" => SecureSocketOptions.None,
            "AUTO" => SecureSocketOptions.Auto,
            _ => SecureSocketOptions.Auto
        };
    }
}
