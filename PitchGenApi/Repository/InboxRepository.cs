using MailKit.Net.Imap;
using MailKit.Net.Pop3;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using PitchGenApi.Database;
using PitchGenApi.Interfaces;
using PitchGenApi.Model;
using PitchGenApi.Model.DTOs;
using System.Linq;
using System.Net.Mail;
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
    
    public async Task<Inboxcredentials?> GetByUserNameAsync(int userId, string username)
    {
        return await _context.Inboxcredentials
                             .FirstOrDefaultAsync(x => x.ClientId == userId && x. Username == username);
    }
    public async Task<bool> ValidateAsync(InboxcredentialsDTO dto)
    {
        try
        {
            
                using var client = new ImapClient();

                // map UI value to SecureSocketOptions
                var option = GetSecureOption(dto.encryption);

                await client.ConnectAsync(dto.Host, dto.Port, option);

                await client.AuthenticateAsync(dto.Username, dto.Password);
                await client.DisconnectAsync(true);

                return true;
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
            // =========================
            // IMAP Inboxes
            // =========================
            var inboxData = await _context.Inboxcredentials
                .Where(x => x.ClientId == clientId)
                .Select(x => new InboxDropdownDto
                {
                    InboxId = x.Id,
                    EmailAddress = x.EmailAddress ?? "",
                    Provider = "IMAP"
                })
                .ToListAsync();

            // =========================
            // OAuth Inboxes
            // =========================
            var oauthData = await _context.EmailOAuthTokens
                .Where(x => x.ClientId == clientId)
                .Select(x => new InboxDropdownDto
                {
                    InboxId = x.Id,
                    EmailAddress = x.Email ?? "",
                    Provider = x.Provider ?? "Unknown"
                })
                .ToListAsync();

            // =========================
            // Merge + Remove duplicates
            // =========================
            var result = inboxData
                .Concat(oauthData)
                .Where(x => !string.IsNullOrEmpty(x.EmailAddress))
                .GroupBy(x => x.EmailAddress.ToLower())
                .Select(g => g.First())
                .OrderBy(x => x.EmailAddress)
                .ToList();

            // =========================
            // Add unread counts
            // =========================
            foreach (var inbox in result)
            {
                // InboxEmails unread count
                // InboxEmails unread count
                // InboxEmails unread count
                inbox.InboxEmailsUnreadCount = await _context.InboxEmails
                    .Where(x =>
                        x.ClientId == clientId &&
                        x.InboxId == inbox.InboxId &&
                        x.IsRead == false &&
                        x.IsDeleted == false &&
                        x.TrackingId != null)
                    .CountAsync();

                // EmailReplies unread count
                inbox.EmailRepliesUnreadCount = await _context.EmailReplies
                    .Where(x =>
                        x.ClientId == clientId &&
                        x.Inboxid == inbox.InboxId &&
                        x.IsRead == false &&
                        x.IsDeleted == false &&
                        x.TrackingId != null)
                    .CountAsync();

                // Total unread count
                inbox.TotalUnreadCount =
                    inbox.InboxEmailsUnreadCount +
                    inbox.EmailRepliesUnreadCount;
            }

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
    public async Task<bool> MarkEmailAsUnassignedReadAsync(string id)
    {
        if (!Guid.TryParse(id, out Guid trackingId))
            return false;
        // Mark InboxEmails as read
        var inboxEmails = await _context.InboxEmails
            .Where(x => x.TrackingId == trackingId)
            .ToListAsync();

        foreach (var item in inboxEmails)
        {
            item.IsRead = true;
        }

        // Mark EmailReplies as read
        var replies = await _context.EmailReplies
            .Where(x => x.TrackingId == trackingId)
            .ToListAsync();

        foreach (var reply in replies)
        {
            reply.IsRead = true;
        }

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
    public async Task<PagedInboxEmailDto> GetInboxThreads(int inboxId, int clientId, string Provider, int pageNumber = 1, int pageSize = 10)
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
        else if (Provider.ToUpper() == "GMAIL" ||
                 Provider.ToUpper() == "OUTLOOK")
        {
            outboxId = inboxId;
        }

        if (outboxId == 0)
        {
            return new PagedInboxEmailDto
            {
                Data = new List<EmailThreadDto>()
            };
        }

        string inboxEmail = "";

        if (Provider.ToUpper() == "IMAP")
        {
            inboxEmail = await _context.Inboxcredentials
                .Where(x => x.Id == inboxId)
                .Select(x => x.Username)
                .FirstOrDefaultAsync() ?? "";
        }
        else if (Provider.ToUpper() == "GMAIL" ||
                 Provider.ToUpper() == "OUTLOOK")
        {
            inboxEmail = await _context.EmailOAuthTokens
                .Where(x => x.Id == inboxId)
                .Select(x => x.Email)
                .FirstOrDefaultAsync() ?? "";
        }

        // =========================
        // SENT EMAILS
        // =========================

        var sentEmails = await _context.EmailLogs
            .Where(x =>
                x.outboxid == outboxId &&
                x.IsSuccess &&
                x.ClientId == clientId &&
                x.IsDeleted == false)
            .ToListAsync();

        var messageIds = sentEmails
            .Where(x => !string.IsNullOrEmpty(x.MessageId))
            .Select(x => x.MessageId)
            .Distinct()
            .ToList();

        var trackingIds = sentEmails
            .Where(x => x.TrackingId != null)
            .Select(x => x.TrackingId)
            .Distinct()
            .ToList();

        // =========================
        // REPLIES
        // =========================
        var pinnedTrackingIds = await _context.PinnedEmails
          .Where(x => x.ClientId == clientId)
          .Select(x => x.TrackingId)
          .ToListAsync();
        List<EmailReplies> replies = new();

        try
        {
            replies = await _context.EmailReplies
                .Where(er =>
                    (
                        (er.TrackingId != null &&
                         trackingIds.Contains(er.TrackingId))

                        ||

                        (er.InReplyTo != null &&
                         messageIds.Contains(er.InReplyTo))

                        ||

                        (er.Inboxid == inboxId)
                    )
                    &&
                    er.IsDeleted == false
                )
                .ToListAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error fetching replies: {ex.Message}");

            // replies empty rahega
            replies = new List<EmailReplies>();
        }
        // =========================
        // INCLUDE TRACKING IDS
        // =========================

        var allTrackingIds = sentEmails
            .Where(x => x.TrackingId != null)
            .Select(x => x.TrackingId)

            .Union(
                replies
                    .Where(x => x.TrackingId != null)
                    .Select(x => x.TrackingId)
            )

            .Distinct()
            .ToList();

        // =========================
        // INBOX EMAILS
        // =========================

        var inboxEmails = await _context.InboxEmails
            .Where(x =>
                x.TrackingId != null &&
                allTrackingIds.Contains(x.TrackingId) &&
                x.IsDeleted == false)
            .ToListAsync();

        allTrackingIds = allTrackingIds
            .Where(trackingId =>
                replies.Any(r => r.TrackingId == trackingId)
                ||
                inboxEmails.Any(i => i.TrackingId == trackingId))
            .ToList();
        // =========================
        // ATTACHMENTS
        // =========================

        var allMessageIds = sentEmails
            .Where(x => !string.IsNullOrWhiteSpace(x.MessageId))
            .Select(x => x.MessageId)

            .Union(
                replies
                    .Where(x => !string.IsNullOrWhiteSpace(x.MessageId))
                    .Select(x => x.MessageId)
            )

            .Union(
                inboxEmails
                    .Where(x => !string.IsNullOrWhiteSpace(x.MessageId))
                    .Select(x => x.MessageId)
            )

            .Distinct()
            .ToList();

        var attachments = await _context.EmailAttachments
            .Where(x => allMessageIds.Contains(x.MessageId))
            .ToListAsync();

        // =========================
        // CONTACT MAP
        // =========================

        var contactIds = sentEmails
            .Where(x => x.ContactId != null)
            .Select(x => x.ContactId.Value)

            .Union(
                replies
                    .Where(x => x.ContactId != null)
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

        var threads = allTrackingIds
            .Select(trackingId =>
            {
                var sentGroup = sentEmails
                    .Where(x => x.TrackingId == trackingId)
                    .ToList();

                var replyGroup = replies
                    .Where(x => x.TrackingId == trackingId)
                    .ToList();

                var inboxGroup = inboxEmails
                    .Where(x => x.TrackingId == trackingId)
                    .ToList();

                var threadMessages = new List<EmailConvDto>();

                var groupContactId =
                    sentGroup.FirstOrDefault(x => x.ContactId != null)?.ContactId
                    ??
                    replyGroup.FirstOrDefault(x => x.ContactId != null)?.ContactId
                    ??
                    0;

                var groupContactName =
                    contactMap.ContainsKey(groupContactId)
                    ? contactMap[groupContactId]
                    : "";

                // =========================
                // INBOX
                // =========================

                threadMessages.AddRange(
                    inboxGroup

                    .Where(i =>
                        !replyGroup.Any(r =>
                            r.MessageId == i.MessageId
                        )
                    )

                    .Select(i => new EmailConvDto
                    {
                        Type = "Inbox",

                        MessageId = i.MessageId,

                        Subject = i.Subject,

                        Body = i.Body,

                        FromEmail = i.FromEmail,

                        ToEmail = inboxEmail,

                        Date = i.Date,

                        IsRead = i.IsRead,

                        ContactId = i.Contactid ?? groupContactId,

                        ContactName = i.FromName,

                        Attachments = attachments
                            .Where(a => a.MessageId == i.MessageId)
                            .Select(a => new EmailAttachmentDto
                            {
                                Id = a.Id,
                                MessageId = a.MessageId,
                                FileName = a.FileName,
                                OriginalFileName = a.OriginalFileName,
                                ContentType = a.ContentType,
                                FilePath = a.FilePath,
                                FileSize = a.FileSize
                            })
                            .ToList()
                    })
                );

                // =========================
                // SENT
                // =========================

                threadMessages.AddRange(
                    sentGroup.Select(s => new EmailConvDto
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

                        ContactName = s.EmailSenderName,

                        Attachments = attachments
                            .Where(a => a.MessageId == s.MessageId)
                            .Select(a => new EmailAttachmentDto
                            {
                                Id = a.Id,
                                MessageId = a.MessageId,
                                FileName = a.FileName,
                                OriginalFileName = a.OriginalFileName,
                                ContentType = a.ContentType,
                                FilePath = a.FilePath,
                                FileSize = a.FileSize
                            })
                            .ToList()
                    })
                );

                // =========================
                // REPLIES
                // =========================

                threadMessages.AddRange(
                    replyGroup.Select(r => new EmailConvDto
                    {
                        Type = "Reply",

                        MessageId = r.MessageId,

                        Subject = r.Subject,

                        Body = r.Body,

                        FromEmail = r.FromEmail,

                        ToEmail = inboxEmail,

                        Date = r.Date,

                        IsRead = r.IsRead ?? false,

                        ContactId = r.ContactId ?? groupContactId,

                        ContactName =
                            contactMap.ContainsKey(r.ContactId ?? groupContactId)
                            ? contactMap[r.ContactId ?? groupContactId]
                            : r.FromEmail,

                        Attachments = attachments
                            .Where(a => a.MessageId == r.MessageId)
                            .Select(a => new EmailAttachmentDto
                            {
                                Id = a.Id,
                                MessageId = a.MessageId,
                                FileName = a.FileName,
                                OriginalFileName = a.OriginalFileName,
                                ContentType = a.ContentType,
                                FilePath = a.FilePath,
                                FileSize = a.FileSize
                            })
                            .ToList()
                    })
                );

                threadMessages = threadMessages
                    .OrderBy(x => x.Date)
                    .ToList();

                return new EmailThreadDto
                {
                    TrackingId = trackingId,

                    IsPinned = trackingId.HasValue &&
                        pinnedTrackingIds.Contains(trackingId.Value),

                    Subject =
                        sentGroup.FirstOrDefault()?.Subject
                        ??
                        replyGroup.FirstOrDefault()?.Subject,

                    ContactEmail =
                        sentGroup.FirstOrDefault()?.ToEmail
                        ??
                        replyGroup.FirstOrDefault()?.FromEmail,

                    TotalMessages = threadMessages.Count,

                    LastMessageDate = threadMessages.Any()
                        ? threadMessages.Max(x => x.Date)
                        : DateTime.MinValue,

                    HasUnread = threadMessages.Any(x =>
                        (x.Type == "Reply" || x.Type == "Inbox")
                        &&
                        !x.IsRead),

                    ContactId = groupContactId,

                    Messages = threadMessages
                };
            })

            .Where(x => x.Messages.Any())

            .OrderByDescending(x => x.LastMessageDate)

            .ToList();

        var totalCount = threads.Count;

        var pagedThreads = threads
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        return new PagedInboxEmailDto
        {
            TotalCount = totalCount,

            TotalPages =
                (int)Math.Ceiling(totalCount / (double)pageSize),

            PageNumber = pageNumber,

            PageSize = pageSize,

            Data = pagedThreads
        };
    }
    public async Task<PagedInboxEmailDto> GetSentOnlyThreads(int inboxId, string Provider, int pageNumber = 1, int pageSize = 10)
    {
        int? outboxId = 0;

        if (Provider.ToUpper() == "IMAP")
        {
            outboxId = await _context.Inboxcredentials
                .Where(x => x.Id == inboxId)
                .Select(x => x.Outboxid)
                .FirstOrDefaultAsync();
        }
        else if (
            Provider.ToUpper() == "GMAIL" ||
            Provider.ToUpper() == "OUTLOOK")
        {
            outboxId = inboxId;
        }

        if (outboxId == 0)
        {
            return new PagedInboxEmailDto
            {
                Data = new List<EmailThreadDto>()
            };
        }

        // =========================================
        // SENT EMAILS
        // =========================================

        var sentEmails = await _context.EmailLogs
            .Where(x =>
                x.outboxid == outboxId &&
                x.IsSuccess &&
                x.IsDeleted == false)
            .ToListAsync();

        // Every successful outbound send is its own Sent item. Its stored Body
        // is the exact snapshot sent at that moment (including any reply trail),
        // so later inbound replies must not change an older Sent item.
        var sentOnlyGroups = sentEmails
            .GroupBy(x => x.Id)
            .ToList();

        var totalCount = sentOnlyGroups.Count;

        var pagedGroups = sentOnlyGroups
            .OrderByDescending(g => g.Max(x => x.SentAt))
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        // =========================================
        // CONTACT MAP
        // =========================================

        var contactIds = pagedGroups.SelectMany(g => g)
            .Where(x => x.ContactId != null).Select(x => x.ContactId!.Value)
            .Distinct()
            .ToList();

        var contactMap = await _context.contacts
            .Where(x => contactIds.Contains(x.id))
            .ToDictionaryAsync(x => x.id, x => x.full_name);

        // =========================================
        // ALL MESSAGE IDS
        // =========================================

        var allMessageIds = pagedGroups.SelectMany(g => g)
            .Where(x => !string.IsNullOrWhiteSpace(x.MessageId)).Select(x => x.MessageId)
            .Distinct()
            .ToList();

        // =========================================
        // ATTACHMENTS
        // =========================================

        var attachments = await _context.EmailAttachments
            .Where(x => allMessageIds.Contains(x.MessageId))
            .ToListAsync();

        // =========================================
        // THREADS
        // =========================================

        var threads = pagedGroups.Select(g =>
        {
            var groupContactId = g
                .FirstOrDefault(x => x.ContactId != null)
                ?.ContactId ?? 0;

            var messages = g
                .Select(s => new EmailConvDto
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

                    ContactName = s.EmailSenderName,

                    Attachments = attachments
                        .Where(a => a.MessageId == s.MessageId)
                        .Select(a => new EmailAttachmentDto
                        {
                            Id = a.Id,
                            MessageId = a.MessageId,
                            FileName = a.FileName,
                            OriginalFileName = a.OriginalFileName,
                            ContentType = a.ContentType,
                            FilePath = a.FilePath,
                            FileSize = a.FileSize
                        })
                        .ToList()
                })
                .OrderBy(x => x.Date)
                .ToList();

            return new EmailThreadDto
            {
                TrackingId = g.First().TrackingId,

                Subject = g.FirstOrDefault()?.Subject,

                ContactEmail = g.FirstOrDefault()?.ToEmail,

                TotalMessages = messages.Count,

                LastMessageDate = messages.Max(x => x.Date),

                HasUnread = false,

                ContactId = groupContactId,

                Messages = messages
            };
        }).ToList();

        return new PagedInboxEmailDto
        {
            TotalCount = totalCount,

            TotalPages = (int)Math.Ceiling(
                totalCount / (double)pageSize),

            PageNumber = pageNumber,

            PageSize = pageSize,

            Data = threads
        };
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

    public async Task<string> DeleteConversationAsync(DeleteConversationDto dto)
    {
        var logs = await _context.EmailLogs
            .Where(x => dto.TrackingIds.Contains(x.TrackingId.Value) && x.ClientId == dto.clientid)
            .ToListAsync();

        var replies = await _context.EmailReplies
            .Where(x => dto.TrackingIds.Contains(x.TrackingId.Value) && x.ClientId == dto.clientid)
            .ToListAsync();

        var inbox = await _context.InboxEmails
            .Where(x => dto.TrackingIds.Contains(x.TrackingId.Value) && x.ClientId == dto.clientid)
            .ToListAsync();

        var Pin = await _context.PinnedEmails
           .Where(x => dto.TrackingIds.Contains(x.TrackingId) && x.ClientId == dto.clientid)
           .ToListAsync();

        if (!logs.Any() && !replies.Any() && !inbox.Any())
            return "Conversation not found";

        if (dto.DeleteMode.Equals("Permanent", StringComparison.OrdinalIgnoreCase))
        {
            _context.EmailLogs.RemoveRange(logs);
            _context.EmailReplies.RemoveRange(replies);
            _context.InboxEmails.RemoveRange(inbox);
            _context.PinnedEmails.RemoveRange(Pin);
        }
        else
        {
            logs.ForEach(x => x.IsDeleted = true);
            replies.ForEach(x => x.IsDeleted = true);
            inbox.ForEach(x => x.IsDeleted = true);
        }

        await _context.SaveChangesAsync();

        return $"Deleted successfully (Logs={logs.Count}, Replies={replies.Count}, Inbox={inbox.Count})";
    }


    public async Task<PagedInboxEmailDto> GetInboxEmails(int clientId, int inboxId, string Provider, int pageNumber = 1, int pageSize = 10)
    {
        dynamic? providerRecord = null;

        string? senderemail = "";

        if (Provider.ToUpper() == "IMAP")
        {
            providerRecord = await _context.Inboxcredentials
                .FirstOrDefaultAsync(x => x.Id == inboxId);

            senderemail = providerRecord?.EmailAddress;
        }
        else if (
            Provider.ToUpper() == "GMAIL" ||
            Provider.ToUpper() == "OUTLOOK")
        {
            providerRecord = await _context.EmailOAuthTokens
                .FirstOrDefaultAsync(x => x.Id == inboxId);

            senderemail = providerRecord?.Email;
        }

        if (providerRecord == null)
        {
            return new PagedInboxEmailDto
            {
                Data = new List<EmailThreadDto>()
            };
        }

        // =========================================
        // INBOX EMAILS
        // =========================================

        var query = _context.InboxEmails
            .Where(x =>
                x.InboxId == inboxId &&
                !x.IsDeleted &&
                x.TrackingId != null)
            .OrderByDescending(x => x.Date);

        var totalCount = await query.CountAsync();

        var inboxEmails = await query
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        // =========================================
        // ALL TRACKING IDS
        // =========================================

        var trackingIds = inboxEmails
            .Where(x => x.TrackingId != null)
            .Select(x => x.TrackingId)
            .Distinct()
            .ToList();

        // =========================================
        // ALL SENT EMAILS
        // =========================================

        var sentEmails = await _context.EmailLogs
            .Where(x =>
                trackingIds.Contains(x.TrackingId) &&
                !x.IsDeleted)
            .OrderBy(x => x.SentAt)
            .ToListAsync();

        // =========================================
        // ALL REPLIES
        // =========================================

        var replies = await _context.EmailReplies
            .Where(x =>
                trackingIds.Contains(x.TrackingId) &&
                x.IsDeleted != true)
            .OrderBy(x => x.Date)
            .ToListAsync();

        var pinnedTrackingIds = await _context.PinnedEmails
          .Where(x => x.ClientId == clientId)
          .Select(x => x.TrackingId)
          .ToListAsync();
        // =========================================
        // ALL MESSAGE IDS
        // =========================================

        var allMessageIds = inboxEmails
            .Select(x => x.MessageId)

            .Union(
                sentEmails.Select(x => x.MessageId)
            )

            .Union(
                replies.Select(x => x.MessageId)
            )

            .Distinct()
            .ToList();

        // =========================================
        // ALL ATTACHMENTS
        // =========================================

        var attachments = await _context.EmailAttachments
            .Where(x => allMessageIds.Contains(x.MessageId))
            .ToListAsync();

        var threads = new List<EmailThreadDto>();

        foreach (var inbox in inboxEmails)
        {
            var trackingId = inbox.TrackingId;

            var threadSentEmails = sentEmails
                .Where(x => x.TrackingId == trackingId)
                .OrderBy(x => x.SentAt)
                .ToList();

            // skip if campaign/sent thread
            if (threadSentEmails.Any())
            {
                continue;
            }

            var threadReplies = replies
                .Where(x => x.TrackingId == trackingId)
                .OrderBy(x => x.Date)
                .ToList();

            var messages = new List<EmailConvDto>();

            // =========================================
            // INBOX MESSAGE
            // =========================================

            messages.Add(new EmailConvDto
            {
                Type = "Inbox",

                MessageId = inbox.MessageId,

                Subject = inbox.Subject,

                Body = inbox.Body,

                FromEmail = inbox.FromEmail,

                ToEmail = inbox.ToEmail,

                Date = inbox.Date,

                IsRead = inbox.IsRead,

                ContactId = inbox.Contactid,

                ContactName = inbox.FromName,

                Attachments = attachments
                    .Where(a => a.MessageId == inbox.MessageId)
                    .Select(a => new EmailAttachmentDto
                    {
                        Id = a.Id,
                        MessageId = a.MessageId,
                        FileName = a.FileName,
                        OriginalFileName = a.OriginalFileName,
                        ContentType = a.ContentType,
                        FilePath = a.FilePath,
                        FileSize = a.FileSize
                    })
                    .ToList()
            });

            // =========================================
            // REPLIES
            // =========================================

            messages.AddRange(
                threadReplies.Select(r => new EmailConvDto
                {
                    Type = "Reply",

                    MessageId = r.MessageId,

                    Subject = r.Subject,

                    Body = r.Body,

                    FromEmail = r.FromEmail,

                    ToEmail = r.ToEmail,

                    Date = r.Date,

                    IsRead = r.IsRead ?? false,

                    ContactId = r.ContactId,

                    Attachments = attachments
                        .Where(a => a.MessageId == r.MessageId)
                        .Select(a => new EmailAttachmentDto
                        {
                            Id = a.Id,
                            MessageId = a.MessageId,
                            FileName = a.FileName,
                            OriginalFileName = a.OriginalFileName,
                            ContentType = a.ContentType,
                            FilePath = a.FilePath,
                            FileSize = a.FileSize
                        })
                        .ToList()
                })
            );

            messages = messages
                .OrderBy(x => x.Date)
                .ToList();

            threads.Add(new EmailThreadDto
            {
                TrackingId = trackingId,

                IsPinned = trackingId.HasValue &&
                        pinnedTrackingIds.Contains(trackingId.Value),

                Subject = inbox.Subject,

                ContactEmail = inbox.FromEmail,

                ContactId = inbox.Contactid,

                TotalMessages = messages.Count,

                LastMessageDate = messages.Max(x => x.Date),

                HasUnread = messages.Any(x =>
                    x.Type == "Reply" && !x.IsRead),

                Messages = messages
            });
        }

        return new PagedInboxEmailDto
        {
            TotalCount = totalCount,

            TotalPages = (int)Math.Ceiling(
                totalCount / (double)pageSize),

            PageNumber = pageNumber,

            PageSize = pageSize,

            Data = threads
        };
    }
    public async Task<TotalUnreadCountDto> GetTotalUnreadCountAsync(int clientId)
    {
        var inboxCredentialIds = await _context.Inboxcredentials
            .Where(x => x.ClientId == clientId)
            .Select(x => x.Id)
            .ToListAsync();

        var oauthIds = await _context.EmailOAuthTokens
            .Where(x => x.ClientId == clientId)
            .Select(x => x.Id)
            .ToListAsync();

        var allInboxIds = inboxCredentialIds
            .Union(oauthIds)
            .ToList();

        var inboxUnreadCount = await _context.InboxEmails
            .Where(x =>
                !x.IsRead &&
                !x.IsDeleted &&
                x.TrackingId != null &&
                allInboxIds.Contains(x.InboxId))
            .CountAsync();

        var repliesUnreadCount = await _context.EmailReplies
             .Where(x =>
                 x.IsRead == false &&
                 x.IsDeleted == false &&
                 x.TrackingId != null &&
                 allInboxIds.Contains(x.Inboxid ?? 0))
             .CountAsync();

        return new TotalUnreadCountDto
        {
            ClientId = clientId,
            GrandTotalUnreadCount = inboxUnreadCount + repliesUnreadCount
        };
    }
    public async Task<PagedInboxEmailDto> GetCombinedInboxThreadsAsync(int clientId, int inboxId, string provider, int pageNumber = 1, int pageSize = 10)
    {
        int? outboxId = 0;

        if (provider.ToUpper() == "IMAP")
        {
            outboxId = await _context.Inboxcredentials
                .Where(x => x.Id == inboxId)
                .Select(x => x.Outboxid)
                .FirstOrDefaultAsync();
        }
        else
        {
            outboxId = inboxId;
        }

        if (outboxId == 0)
        {
            return new PagedInboxEmailDto
            {
                Data = new List<EmailThreadDto>()
            };
        }

        // =========================================
        // SENT EMAILS
        // =========================================

        var sentEmails = await _context.EmailLogs
            .Where(x =>
                x.outboxid == outboxId &&
                x.IsSuccess &&
                !x.IsDeleted)
            .ToListAsync();

        // =========================================
        // INBOX EMAILS
        // =========================================

        var inboxEmails = await _context.InboxEmails
            .Where(x =>
                x.ClientId == clientId &&
                x.InboxId == inboxId &&
                !x.IsDeleted &&
                x.TrackingId != null)
            .ToListAsync();

        // =========================================
        // REPLIES
        // =========================================

        var replies = await _context.EmailReplies
            .Where(x =>
                x.ClientId == clientId &&
                x.IsDeleted != true &&
                x.Inboxid == inboxId)
            .ToListAsync();

        var pinnedTrackingIds = await _context.PinnedEmails
            .Where(x => x.ClientId == clientId)
            .Select(x => x.TrackingId)
            .ToListAsync();

        // =========================================
        // ALL TRACKING IDS
        // =========================================

        var allTrackingIds = sentEmails
            .Where(x => x.TrackingId != null)
            .Select(x => x.TrackingId)

            .Union(
                inboxEmails
                .Where(x => x.TrackingId != null)
                .Select(x => x.TrackingId)
            )

            .Union(
                replies
                .Where(x => x.TrackingId != null)
                .Select(x => x.TrackingId)
            )

            .Distinct()
            .ToList();

        // =========================================
        // ALL MESSAGE IDS
        // =========================================

        var allMessageIds = sentEmails
            .Select(x => x.MessageId)

            .Union(inboxEmails.Select(x => x.MessageId))

            .Union(replies.Select(x => x.MessageId))

            .Where(x => !string.IsNullOrWhiteSpace(x))

            .Distinct()

            .ToList();

        // =========================================
        // ATTACHMENTS
        // =========================================

        var attachments = await _context.EmailAttachments
            .Where(x => allMessageIds.Contains(x.MessageId))
            .ToListAsync();

        // =========================================
        // CONTACT MAP
        // =========================================

        var contactIds = replies
            .Where(x => x.ContactId != null)
            .Select(x => x.ContactId.Value)
            .Distinct()
            .ToList();

        var contactMap = await _context.contacts
            .Where(x => contactIds.Contains(x.id))
            .ToDictionaryAsync(x => x.id, x => x.full_name);

        // =========================================
        // THREAD BUILD
        // =========================================

        var threads = allTrackingIds
            .Select(trackingId =>
            {
                var messages = new List<EmailConvDto>();

                // =========================================
                // INBOX
                // =========================================

                messages.AddRange(
                    inboxEmails
                    .Where(i => i.TrackingId == trackingId)
                    .Select(i => new EmailConvDto
                    {
                        Type = "Inbox",

                        MessageId = i.MessageId,

                        Subject = i.Subject,

                        Body = i.Body,

                        FromEmail = i.FromEmail,

                        ToEmail = i.ToEmail,

                        Date = i.Date,

                        IsRead = i.IsRead,

                        ContactId = i.Contactid,

                        ContactName = i.FromName,

                        Attachments = attachments
                            .Where(a => a.MessageId == i.MessageId)
                            .Select(a => new EmailAttachmentDto
                            {
                                Id = a.Id,
                                MessageId = a.MessageId,
                                FileName = a.FileName,
                                OriginalFileName = a.OriginalFileName,
                                ContentType = a.ContentType,
                                FilePath = a.FilePath,
                                FileSize = a.FileSize
                            })
                            .ToList()
                    })
                );

                // =========================================
                // SENT
                // =========================================

                messages.AddRange(
                    sentEmails
                    .Where(s => s.TrackingId == trackingId)
                    .Select(s => new EmailConvDto
                    {
                        Type = "Sent",

                        MessageId = s.MessageId,

                        Subject = s.Subject,

                        Body = s.Body,

                        FromEmail = s.SenderEmailId,

                        ToEmail = s.ToEmail,

                        Date = s.SentAt,

                        IsRead = true,

                        ContactId = s.ContactId,

                        ContactName = s.EmailSenderName,

                        Attachments = attachments
                            .Where(a => a.MessageId == s.MessageId)
                            .Select(a => new EmailAttachmentDto
                            {
                                Id = a.Id,
                                MessageId = a.MessageId,
                                FileName = a.FileName,
                                OriginalFileName = a.OriginalFileName,
                                ContentType = a.ContentType,
                                FilePath = a.FilePath,
                                FileSize = a.FileSize
                            })
                            .ToList()
                    })
                );

                // =========================================
                // REPLIES
                // =========================================

                messages.AddRange(
                    replies
                    .Where(r => r.TrackingId == trackingId)
                    .Select(r => new EmailConvDto
                    {
                        Type = "Reply",

                        MessageId = r.MessageId,

                        Subject = r.Subject,

                        Body = r.Body,

                        FromEmail = r.FromEmail,

                        ToEmail = r.ToEmail,

                        Date = r.Date,

                        IsRead = r.IsRead ?? false,

                        ContactId = r.ContactId,

                        ContactName = contactMap.ContainsKey(r.ContactId ?? 0)
                            ? contactMap[r.ContactId ?? 0]
                            : r.FromEmail,

                        Attachments = attachments
                            .Where(a => a.MessageId == r.MessageId)
                            .Select(a => new EmailAttachmentDto
                            {
                                Id = a.Id,
                                MessageId = a.MessageId,
                                FileName = a.FileName,
                                OriginalFileName = a.OriginalFileName,
                                ContentType = a.ContentType,
                                FilePath = a.FilePath,
                                FileSize = a.FileSize
                            })
                            .ToList()
                    })
                );

                var hasInbox = inboxEmails.Any(i => i.TrackingId == trackingId);

                var hasReply = replies.Any(r => r.TrackingId == trackingId);

                if (!hasInbox && !hasReply)
                {
                    return null;
                }

                messages = messages
                    .OrderBy(x => x.Date)
                    .ToList();

                // =========================================
                // FIRST RECORDS
                // =========================================

                var inboxFirst = inboxEmails
                    .Where(i => i.TrackingId == trackingId)
                    .OrderBy(i => i.Date)
                    .FirstOrDefault();

                var sentFirst = sentEmails
                    .Where(s => s.TrackingId == trackingId)
                    .OrderBy(s => s.SentAt)
                    .FirstOrDefault();

                var replyFirst = replies
                    .Where(r => r.TrackingId == trackingId)
                    .OrderBy(r => r.Date)
                    .FirstOrDefault();

                // =========================================
                // SKIP EMPTY THREAD
                // =========================================

                if (!messages.Any())
                    return null;

                return new EmailThreadDto
                {
                    TrackingId = trackingId,

                    IsPinned = trackingId.HasValue &&
                            pinnedTrackingIds.Contains(trackingId.Value),

                    Subject =
                        inboxFirst?.Subject ??
                        sentFirst?.Subject ??
                        replyFirst?.Subject,

                    ContactEmail =
                        inboxFirst?.FromEmail ??
                        sentFirst?.ToEmail ??
                        replyFirst?.FromEmail,

                    ContactId =
                        inboxFirst?.Contactid ??
                        sentFirst?.ContactId ??
                        replyFirst?.ContactId,

                    TotalMessages = messages.Count,

                    LastMessageDate = messages.Max(x => x.Date),

                    HasUnread = messages.Any(x => !x.IsRead),

                    Messages = messages
                };
            })
            .Where(x => x != null)
            .OrderByDescending(x => x.LastMessageDate)
            .ToList();

        // =========================================
        // PAGINATION
        // =========================================

        var totalCount = threads.Count;

        var pagedThreads = threads
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        return new PagedInboxEmailDto
        {
            TotalCount = totalCount,

            TotalPages =
                (int)Math.Ceiling(totalCount / (double)pageSize),

            PageNumber = pageNumber,

            PageSize = pageSize,

            Data = pagedThreads
        };
    }


    public async Task<bool> CreateInboxCredentialsAsync(InboxcredentialsDTO dto)
    {
        try
        {
            // =========================
            // CHECK EXISTING
            // =========================
            var existing = await GetByUserNameAsync(
                dto.ClientId,
                dto.Username);

            if (existing != null)
                return false;

            // =========================
            // CHECK SMTP
            // =========================
            var smtp = await _context.SmtpCredentials
                .FirstOrDefaultAsync(s =>
                    s.Username == dto.Username &&
                    s.ClientId == dto.ClientId.ToString());

            if (smtp == null)
                return false;

            // =========================
            // VALIDATE IMAP
            // =========================
            var isValid = await ValidateAsync(dto);

            if (!isValid)
                return false;

            // =========================
            // CREATE ENTITY
            // =========================
            var entity = new Inboxcredentials
            {
                ClientId = dto.ClientId,
                EmailAddress = dto.EmailAddress,
                Protocol = "IMAP",
                Host = dto.Host,
                Port = dto.Port,
                Username = dto.Username,
                Password = dto.Password,
                Outboxid = smtp.Id,
                encryption = dto.encryption,
                FullInboxSync = dto.FullInboxSync,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            await AddAsync(entity);

            return true;
        }
        catch
        {
            return false;
        }
    }
    public async Task<string> TogglePinAsync(int clientId, Guid trackingId)
    {
        var existingPin = await _context.PinnedEmails
            .FirstOrDefaultAsync(x =>
                x.ClientId == clientId &&
                x.TrackingId == trackingId);

        if (existingPin != null)
        {
            _context.PinnedEmails.Remove(existingPin);
            await _context.SaveChangesAsync();

            return "Email unpinned successfully.";
        }

        var pin = new PinnedEmails
        {
            ClientId = clientId,
            TrackingId = trackingId,
            CreatedAt = DateTime.UtcNow
        };

        _context.PinnedEmails.Add(pin);
        await _context.SaveChangesAsync();

        return "Email pinned successfully.";
    }
    public async Task<List<EmailThreadDto>> GetPinnedEmails(int clientId, int contactId)
    {
        var pinnedTrackingIds = await _context.PinnedEmails
            .Where(x => x.ClientId == clientId)
            .Select(x => x.TrackingId)
            .ToListAsync();

        var inboxEmails = await _context.InboxEmails
            .Where(x =>
                x.Contactid == contactId &&
                x.TrackingId != null &&
                pinnedTrackingIds.Contains(x.TrackingId.Value))
            .ToListAsync();

        var replies = await _context.EmailReplies
            .Where(x =>
                x.ContactId == contactId &&
                x.TrackingId != null &&
                pinnedTrackingIds.Contains(x.TrackingId.Value))
            .ToListAsync();

        var sentEmails = await _context.EmailLogs
            .Where(x =>
                x.ContactId == contactId &&
                x.TrackingId != null &&
                pinnedTrackingIds.Contains(x.TrackingId.Value))
            .ToListAsync();

        var trackingIds = pinnedTrackingIds
            .Distinct()
            .ToList();

        var allMessageIds = inboxEmails
            .Select(x => x.MessageId)

            .Union(sentEmails.Select(x => x.MessageId))

            .Union(replies.Select(x => x.MessageId))

            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct()
            .ToList();

        var attachments = await _context.EmailAttachments
            .Where(x => allMessageIds.Contains(x.MessageId))
            .ToListAsync();

        var contact = await _context.contacts
            .FirstOrDefaultAsync(x => x.id == contactId);

        var threads = trackingIds
            .Select(trackingId =>
            {
                var messages = new List<EmailConvDto>();

                messages.AddRange(
                    inboxEmails
                    .Where(x => x.TrackingId == trackingId)
                    .Select(i => new EmailConvDto
                    {
                        Type = "Inbox",
                        MessageId = i.MessageId,
                        Subject = i.Subject,
                        Body = i.Body,
                        FromEmail = i.FromEmail,
                        ToEmail = i.ToEmail,
                        Date = i.Date,
                        IsRead = i.IsRead,
                        ContactId = i.Contactid,
                        ContactName = i.FromName,
                        Attachments = attachments
                            .Where(a => a.MessageId == i.MessageId)
                            .Select(a => new EmailAttachmentDto
                            {
                                Id = a.Id,
                                MessageId = a.MessageId,
                                FileName = a.FileName,
                                OriginalFileName = a.OriginalFileName,
                                ContentType = a.ContentType,
                                FilePath = a.FilePath,
                                FileSize = a.FileSize
                            })
                            .ToList()
                    })
                );

                messages.AddRange(
                    sentEmails
                    .Where(x => x.TrackingId == trackingId)
                    .Select(s => new EmailConvDto
                    {
                        Type = "Sent",
                        MessageId = s.MessageId,
                        Subject = s.Subject,
                        Body = s.Body,
                        FromEmail = s.SenderEmailId,
                        ToEmail = s.ToEmail,
                        Date = s.SentAt,
                        IsRead = true,
                        ContactId = s.ContactId,
                        ContactName = s.EmailSenderName,
                        Attachments = attachments
                            .Where(a => a.MessageId == s.MessageId)
                            .Select(a => new EmailAttachmentDto
                            {
                                Id = a.Id,
                                MessageId = a.MessageId,
                                FileName = a.FileName,
                                OriginalFileName = a.OriginalFileName,
                                ContentType = a.ContentType,
                                FilePath = a.FilePath,
                                FileSize = a.FileSize
                            })
                            .ToList()
                    })
                );

                messages.AddRange(
                    replies
                    .Where(x => x.TrackingId == trackingId)
                    .Select(r => new EmailConvDto
                    {
                        Type = "Reply",
                        MessageId = r.MessageId,
                        Subject = r.Subject,
                        Body = r.Body,
                        FromEmail = r.FromEmail,
                        ToEmail = r.ToEmail,
                        Date = r.Date,
                        IsRead = r.IsRead ?? false,
                        ContactId = r.ContactId,
                        ContactName = contact?.full_name ?? r.FromEmail,
                        Attachments = attachments
                            .Where(a => a.MessageId == r.MessageId)
                            .Select(a => new EmailAttachmentDto
                            {
                                Id = a.Id,
                                MessageId = a.MessageId,
                                FileName = a.FileName,
                                OriginalFileName = a.OriginalFileName,
                                ContentType = a.ContentType,
                                FilePath = a.FilePath,
                                FileSize = a.FileSize
                            })
                            .ToList()
                    })
                );

                messages = messages
                    .OrderBy(x => x.Date)
                    .ToList();

                if (!messages.Any())
                    return null;

                return new EmailThreadDto
                {
                    TrackingId = trackingId,
                    IsPinned = true,
                    Subject = messages.FirstOrDefault()?.Subject,
                    ContactEmail =
                        inboxEmails.FirstOrDefault(x => x.TrackingId == trackingId)?.FromEmail
                        ??
                        sentEmails.FirstOrDefault(x => x.TrackingId == trackingId)?.ToEmail,
                    ContactId = contactId,
                    TotalMessages = messages.Count,
                    LastMessageDate = messages.Max(x => x.Date),
                    HasUnread = messages.Any(x => !x.IsRead),
                    Messages = messages
                };
            })
            .Where(x => x != null)
            .OrderByDescending(x => x.LastMessageDate)
            .ToList();

        return threads;
    }

    public async Task<string?> GetLatestEmailTrailAsync(Guid trackingId)
    {
        var latestSent = await _context.EmailLogs
            .Where(x => x.TrackingId == trackingId)
            .OrderByDescending(x => x.SentAt)
            .Select(x => new
            {
                Date = (DateTime?)x.SentAt,
                Subject = x.Subject,
                Body = x.Body,
                From = x.EmailSenderName ?? x.SenderEmailId,
                To = x.ToEmail
            })
            .FirstOrDefaultAsync();

        var latestInbox = await _context.InboxEmails
            .Where(x => x.TrackingId == trackingId)
            .OrderByDescending(x => x.Date)
            .Select(x => new
            {
                Date = (DateTime?)x.Date,
                Subject = x.Subject,
                Body = x.Body,
                From = x.FromName ?? x.FromEmail,
                To = x.ToEmail
            })
            .FirstOrDefaultAsync();

        var latestReply = await _context.EmailReplies
            .Where(x => x.TrackingId == trackingId)
            .OrderByDescending(x => x.Date)
            .Select(x => new
            {
                Date = (DateTime?)x.Date,
                Subject = x.Subject,
                Body = x.Body,
                From = x.FromEmail,
                To = x.ToEmail
            })
            .FirstOrDefaultAsync();

        var emails = new List<dynamic>();

        if (latestSent != null) emails.Add(latestSent);
        if (latestInbox != null) emails.Add(latestInbox);
        if (latestReply != null) emails.Add(latestReply);

        var latest = emails
            .OrderByDescending(x => x.Date)
            .FirstOrDefault();

        if (latest == null)
            return null;

        return
            $"From: {latest.From}\r\n" +
            $"Sent: {latest.Date:dddd, MMMM dd, yyyy h:mm tt}\r\n" +
            (!string.IsNullOrWhiteSpace(latest.To)
                ? $"To: {latest.To}\r\n"
                : "") +
            $"Subject: {latest.Subject}\r\n\r\n" +
            latest.Body;
    }
}
