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
                inbox.InboxEmailsUnreadCount = await _context.InboxEmails
                    .Where(x =>
                        x.ClientId == clientId &&
                        x.InboxId == inbox.InboxId &&
                        x.IsRead == false &&
                        x.IsDeleted == false)
                    .CountAsync();

                // EmailReplies unread count
                // EmailReplies unread count
                inbox.EmailRepliesUnreadCount = await (
                    from reply in _context.EmailReplies
                    join inboxEmail in _context.InboxEmails
                        on reply.TrackingId equals inboxEmail.TrackingId
                    where reply.ClientId == clientId
                          && inboxEmail.InboxId == inbox.InboxId
                          && reply.IsRead == false
                          && reply.IsDeleted == false
                    select reply.Id
                ).CountAsync();

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
    public async Task<PagedInboxEmailDto> GetInboxThreads(int inboxId, string Provider, int pageNumber = 1, int pageSize = 10)
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
            return new PagedInboxEmailDto { Data = new List<EmailThreadDto>() };

        // =========================
        // SENT EMAILS
        // =========================
        var sentEmails = await _context.EmailLogs
            .Where(x => x.outboxid == outboxId && x.IsSuccess && x.IsDeleted == false)
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
                 (
                     (er.TrackingId != null && trackingIds.Contains(er.TrackingId))
                     ||
                     (er.InReplyTo != null && messageIds.Contains(er.InReplyTo))
                 )
                 && er.IsDeleted == false
             )
             .ToListAsync();

        // =========================
        // INBOX EMAILS
        // =========================
        var inboxEmails = await _context.InboxEmails
            .Where(x =>
                x.TrackingId != null &&
                trackingIds.Contains(x.TrackingId) &&
                x.IsDeleted == false)
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
            .Where(g =>
                    replies.Any(r => r.TrackingId == g.Key) ||
                    inboxEmails.Any(i => i.TrackingId == g.Key)
                     )
            .Select(g =>
            {
                var threadMessages = new List<EmailConvDto>();

                var groupContactId = g.FirstOrDefault(x => x.ContactId != null)?.ContactId ?? 0;

                var groupContactName = contactMap.ContainsKey(groupContactId)
                    ? contactMap[groupContactId]
                    : "";
                // INBOX
                threadMessages.AddRange(
                    inboxEmails
                    .Where(i => i.TrackingId == g.Key)
                    .Select(i => new EmailConvDto
                    {
                        Type = "Inbox",
                        MessageId = i.MessageId,
                        Subject = i.Subject,
                        Body = i.Body,
                        FromEmail = i.FromEmail,
                        ToEmail = "",
                        Date = i.Date,
                        IsRead = i.IsRead,
                        ContactId = i.Contactid ?? groupContactId,
                        ContactName = i.FromName
                    })
                );
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

        var totalCount = threads.Count;
        var pagedThreads = threads
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        return new PagedInboxEmailDto
        {
            TotalCount = totalCount,
            TotalPages = (int)Math.Ceiling(totalCount / (double)pageSize),
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
        else if (Provider.ToUpper() == "GMAIL" || Provider.ToUpper() == "OUTLOOK")
        {
            outboxId = inboxId;
        }

        if (outboxId == 0)
            return new PagedInboxEmailDto { Data = new List<EmailThreadDto>() };

        var sentEmails = await _context.EmailLogs
            .Where(x => x.outboxid == outboxId && x.IsSuccess && x.IsDeleted == false)
            .ToListAsync();

        var trackingIds = sentEmails
            .Where(x => x.TrackingId != null)
            .Select(x => x.TrackingId)
            .Distinct()
            .ToList();

        var messageIds = sentEmails
            .Where(x => !string.IsNullOrEmpty(x.MessageId))
            .Select(x => x.MessageId)
            .ToList();

        var repliedTrackingIds = await _context.EmailReplies
            .Where(er =>
                er.IsDeleted == false &&
                (
                    (er.TrackingId != null && trackingIds.Contains(er.TrackingId))
                    || (er.InReplyTo != null && messageIds.Contains(er.InReplyTo))
                )
            )
            .Select(er => er.TrackingId)
            .Distinct()
            .ToListAsync();

        var inboxRepliedTrackingIds = await _context.InboxEmails
            .Where(x => x.TrackingId != null && trackingIds.Contains(x.TrackingId) && x.IsDeleted == false)
            .Select(x => x.TrackingId)
            .Distinct()
            .ToListAsync();

        var excludedTrackingIds = repliedTrackingIds
            .Union(inboxRepliedTrackingIds)
            .Distinct()
            .ToList();

        var sentOnlyGroups = sentEmails
            .GroupBy(x => x.TrackingId)
            .Where(g => !excludedTrackingIds.Contains(g.Key))
            .ToList();

        var totalCount = sentOnlyGroups.Count;

        var pagedGroups = sentOnlyGroups
            .OrderByDescending(g => g.Max(x => x.SentAt))
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        var contactIds = pagedGroups
            .SelectMany(g => g)
            .Where(x => x.ContactId != null)
            .Select(x => x.ContactId.Value)
            .Distinct()
            .ToList();

        var contactMap = await _context.contacts
            .Where(x => contactIds.Contains(x.id))
            .ToDictionaryAsync(x => x.id, x => x.full_name);

        var threads = pagedGroups.Select(g =>
        {
            var groupContactId = g.FirstOrDefault(x => x.ContactId != null)?.ContactId ?? 0;
            var messages = g.Select(s => new EmailConvDto
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
                ContactName = s.EmailSenderName
            }).OrderBy(x => x.Date).ToList();

            return new EmailThreadDto
            {
                TrackingId = g.Key,
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
            TotalPages = (int)Math.Ceiling(totalCount / (double)pageSize),
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

        if (!logs.Any() && !replies.Any() && !inbox.Any())
            return "Conversation not found";

        if (dto.DeleteMode.Equals("Permanent", StringComparison.OrdinalIgnoreCase))
        {
            _context.EmailLogs.RemoveRange(logs);
            _context.EmailReplies.RemoveRange(replies);
            _context.InboxEmails.RemoveRange(inbox);
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


    public async Task<PagedInboxEmailDto> GetInboxEmails(int clientId, int inboxId,string Provider, int pageNumber = 1, int pageSize = 10)
    {
        int? outboxId = 0;

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
            return new PagedInboxEmailDto { Data = new List<EmailThreadDto>() };

        var query = _context.InboxEmails
            .Where(x => x.InboxId == inboxId && !x.IsDeleted && x.TrackingId != null)
            .OrderByDescending(x => x.Date);

        var totalCount = await query.CountAsync();

        var inboxEmails = await query
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        var threads = new List<EmailThreadDto>();

        foreach (var inbox in inboxEmails)
        {
            var trackingId = inbox.TrackingId;

            var sentEmails = await _context.EmailLogs
                .Where(x => x.TrackingId == trackingId && !x.IsDeleted)
                .OrderBy(x => x.SentAt)
                .ToListAsync();

            if (sentEmails.Any())
            {
                continue;
            }
            var replies = await _context.EmailReplies
                .Where(x => x.TrackingId == trackingId && x.IsDeleted != true)
                .OrderBy(x => x.Date)
                .ToListAsync();

            var messages = new List<EmailConvDto>();

            messages.Add(new EmailConvDto
            {
                Type = "Inbox",
                MessageId = inbox.MessageId,
                Subject = inbox.Subject,
                Body = inbox.Body,
                FromEmail = inbox.FromEmail,
                ToEmail = "",
                Date = inbox.Date,
                IsRead = inbox.IsRead,
                ContactId = inbox.Contactid,
                ContactName = inbox.FromName
            });
            messages.AddRange(replies.Select(r => new EmailConvDto
            {
                Type = "Reply",
                MessageId = r.MessageId,
                Subject = r.Subject,
                Body = r.Body,
                FromEmail = r.FromEmail,
                ToEmail = "",
                Date = r.Date,
                IsRead = r.IsRead ?? false,
                ContactId = r.ContactId
            }));

            messages = messages.OrderBy(x => x.Date).ToList();

            threads.Add(new EmailThreadDto
            {
                TrackingId = trackingId,
                Subject = inbox.Subject,
                ContactEmail = inbox.FromEmail,
                ContactId = inbox.Contactid,
                TotalMessages = messages.Count,
                LastMessageDate = messages.Max(x => x.Date),
                HasUnread = messages.Any(x => x.Type == "Reply" && !x.IsRead),
                Messages = messages
            });
        }

        return new PagedInboxEmailDto
        {
            TotalCount = totalCount,
            TotalPages = (int)Math.Ceiling(totalCount / (double)pageSize),
            PageNumber = pageNumber,
            PageSize = pageSize,
            Data = threads
        };
    }
    public async Task<TotalUnreadCountDto> GetTotalUnreadCountAsync(int clientId)
    {
        // InboxEmails unread count
        var inboxUnreadCount = await _context.InboxEmails
            .Where(x =>
                x.ClientId == clientId &&
                x.IsRead == false &&
                x.IsDeleted == false)
            .CountAsync();

        // EmailReplies unread count
        var repliesUnreadCount = await _context.EmailReplies
            .Where(x =>
                x.ClientId == clientId &&
                x.IsRead == false &&
                x.IsDeleted == false)
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
            return new PagedInboxEmailDto { Data = new List<EmailThreadDto>() };

        // BATCH FETCH
        var sentEmails = await _context.EmailLogs
            .Where(x => x.outboxid == outboxId && x.IsSuccess && !x.IsDeleted)
            .ToListAsync();

        var inboxEmails = await _context.InboxEmails
            .Where(x => x.ClientId == clientId && x.InboxId == inboxId && !x.IsDeleted && x.TrackingId != null)
            .ToListAsync();

        var allTrackingIds = sentEmails.Select(x => x.TrackingId)
            .Union(inboxEmails.Select(x => x.TrackingId))
            .Distinct()
            .ToList();

        var replies = await _context.EmailReplies
            .Where(x => x.ClientId == clientId && allTrackingIds.Contains(x.TrackingId) && x.IsDeleted != true)
            .ToListAsync();
        var contactIds = replies
            .Where(x => x.ContactId != null)
            .Select(x => x.ContactId.Value)
            .Distinct()
            .ToList();

        var contactMap = await _context.contacts
            .Where(x => contactIds.Contains(x.id))
            .ToDictionaryAsync(x => x.id, x => x.full_name);

        // MERGE BY TRACKING ID
        var threads = allTrackingIds.Select(trackingId =>
        {
            var messages = new List<EmailConvDto>();

            // INBOX
            messages.AddRange(inboxEmails
                .Where(i => i.TrackingId == trackingId)
                .Select(i => new EmailConvDto
                {
                    Type = "Inbox",
                    MessageId = i.MessageId,
                    Subject = i.Subject,
                    Body = i.Body,
                    FromEmail = i.FromEmail,
                    ToEmail = "",
                    Date = i.Date,
                    IsRead = i.IsRead,
                    ContactId = i.Contactid,
                    ContactName = i.FromName
                }));

            // SENT
            messages.AddRange(sentEmails
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
                    ContactName = s.EmailSenderName
                }));

            // REPLIES
            messages.AddRange(replies
                .Where(r => r.TrackingId == trackingId)
                .Select(r => new EmailConvDto
                {
                    Type = "Reply",
                    MessageId = r.MessageId,
                    Subject = r.Subject,
                    Body = r.Body,
                    FromEmail = r.FromEmail,
                    ToEmail = "",
                    Date = r.Date,
                    IsRead = r.IsRead ?? false,
                    ContactId = r.ContactId,
                    ContactName = contactMap.ContainsKey(r.ContactId ?? 0)
                        ? contactMap[r.ContactId ?? 0]
                        : r.FromEmail
                }));

            messages = messages.OrderBy(x => x.Date).ToList();

            var inboxFirst = inboxEmails.FirstOrDefault(i => i.TrackingId == trackingId);
            var sentFirst = sentEmails.FirstOrDefault(s => s.TrackingId == trackingId);

            return new EmailThreadDto
            {
                TrackingId = trackingId,
                Subject = inboxFirst?.Subject ?? sentFirst?.Subject,
                ContactEmail = inboxFirst?.FromEmail ?? sentFirst?.ToEmail,
                ContactId = inboxFirst?.Contactid ?? sentFirst?.ContactId,
                TotalMessages = messages.Count,
                LastMessageDate = messages.Max(x => x.Date),
                HasUnread = messages.Any(x => !x.IsRead),
                Messages = messages
            };
        })
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
            TotalPages = (int)Math.Ceiling(totalCount / (double)pageSize),
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
}
