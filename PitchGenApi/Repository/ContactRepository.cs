using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using PitchGenApi.Database;
using PitchGenApi.Model;
using PitchGenApi.Model.DTOs;
using PitchGenApi.Models;
using PitchGenApi.Services;
using Serilog;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using static ContactRepository;

public class ContactRepository
{
    private readonly AppDbContext _context;

    public ContactRepository(AppDbContext context)
    {
        _context = context;
    }

    public async Task<List<Contact>> GetContactsAsync(int? DataFileId)
    {
        var query = _context.contacts.AsQueryable();

        if (DataFileId.HasValue)
        {
            query = query.Where(c => c.DataFileId == DataFileId.Value);
        }

        return await query.ToListAsync();
    }


    /// <summary>
    /// Whether this client is an admin. The extension needs this to decide
    /// who may see unlock diagnostics, and the JWT carries no admin claim -
    /// the same token is issued to the web app, so the flag stays in the
    /// database rather than in the token.
    /// </summary>
    public async Task<bool> IsAdminAsync(int clientId)
    {
        if (clientId <= 0)
            return false;

        return await _context.ClientDetails
            .AsNoTracking()
            .Where(c => c.Id == clientId)
            .Select(c => c.IsAdmin)
            .FirstOrDefaultAsync();
    }

    public async Task<bool> HasAvailableCreditAsync(int clientId)
    {
        var finalCredit = await _context.FinalUserCredit
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.ClientId == clientId);

        if (finalCredit == null)
            return false;

        if ((finalCredit.TotalCredit ?? 0) > 0 &&
            (finalCredit.LimitUsed ?? 0) < (finalCredit.MonthlyLimit ?? 0))
        {
            return true;
        }

        if ((finalCredit.CustomLimit ?? 0) <= 0)
            return false;

        return await _context.UserCredits
            .AsNoTracking()
            .AnyAsync(u => u.ClientId == clientId &&
                           u.Status.ToLower() == "active" &&
                           (u.Plane == "Custom Credit" || u.Plane == "Internal") &&
                           (u.Credits ?? 0) > 0);
    }

    public async Task<ContactWithNextDto> GetContactWithNextAsync(int dataFileId, int? contactId = null)
    {
        Contact currentContact;

        if (contactId.HasValue)
        {
            currentContact = await _context.contacts
                .FirstOrDefaultAsync(c => c.DataFileId == dataFileId && c.id == contactId.Value);
        }
        else
        {
            currentContact = await _context.contacts
                .Where(c => c.DataFileId == dataFileId)
                .OrderBy(c => c.id)
                .FirstOrDefaultAsync();
        }

        if (currentContact == null)
            return null;

        var nextContactId = await _context.contacts
            .Where(c => c.DataFileId == dataFileId && c.id > currentContact.id)
            .OrderBy(c => c.id)
            .Select(c => (int?)c.id)
            .FirstOrDefaultAsync();

        return new ContactWithNextDto
        {
            CurrentContact = currentContact,
            NextContactId = nextContactId
        };
    }
    public async Task<List<Contact>?> GetContactBySegment(int? SegmentId)
    {
        if (!SegmentId.HasValue)
            return null;

        return await _context.segmentContacts
               .Where(sc => sc.SegmentId == SegmentId.Value)
               .Include(sc => sc.Contact)
               .Select(sc => sc.Contact)
               .ToListAsync();
    }

    public async Task<string> BuildEmailThreadAsync(int clientId, int? datafileid, int contactid, int? segmentid)
    {
        var logsQuery = _context.EmailLogs
            .AsNoTracking()
            .Where(x => x.ClientId == clientId
                        && x.ContactId == contactid
                        && x.IsSuccess == true);

        if (datafileid != null && segmentid == null)
        {
            logsQuery = logsQuery.Where(x => x.DataFileId == datafileid && x.SegmentId == null);
        }
        else if (segmentid != null)
        {
            logsQuery = logsQuery.Where(x =>
                x.SegmentId == segmentid ||
                (datafileid != null && x.DataFileId == datafileid && x.SegmentId == null)
            );
        }

        var logs = await logsQuery
            .OrderByDescending(x => x.SentAt)
            .ToListAsync();

        var inboxEmails = await _context.InboxEmails
            .AsNoTracking()
            .Where(x => x.ClientId == clientId
                        && x.Contactid == contactid
                        && !x.IsDeleted)
            .ToListAsync();

        var replies = await _context.EmailReplies
            .AsNoTracking()
            .Where(x => x.ClientId == clientId
                        && x.ContactId == contactid
                        && x.IsDeleted != true)
            .ToListAsync();

        var threadMessages = logs.Select(log => new EmailThreadMessage
        {
            MessageId = log.MessageId,
            FromName = log.EmailSenderName,
            FromEmail = log.SenderEmailId,
            ToName = log.EmailRecipientName,
            ToEmail = log.ToEmail,
            Subject = log.Subject,
            Body = log.Body,
            Date = log.SentAt
        })
        .Concat(inboxEmails.Select(inbox => new EmailThreadMessage
        {
            MessageId = inbox.MessageId,
            FromName = inbox.FromName,
            FromEmail = inbox.FromEmail,
            ToEmail = inbox.ToEmail,
            Subject = inbox.Subject,
            Body = inbox.Body,
            Date = inbox.Date
        }))
        .Concat(replies.Select(reply => new EmailThreadMessage
        {
            MessageId = reply.MessageId,
            FromName = reply.FromName,
            FromEmail = reply.FromEmail,
            ToEmail = reply.ToEmail,
            Subject = reply.Subject,
            Body = reply.Body,
            Date = reply.Date ?? reply.CreatedAt
        }))
        .Where(x => x.Date != null)
        .GroupBy(x => string.IsNullOrWhiteSpace(x.MessageId)
            ? $"{x.FromEmail}|{x.ToEmail}|{x.Subject}|{x.Date:O}"
            : x.MessageId)
        .Select(x => x.First())
        .OrderByDescending(x => x.Date)
        .ToList();

        if (!threadMessages.Any())
            return "";

        StringBuilder sb = new StringBuilder();

        foreach (var message in threadMessages)
        {
            sb.AppendLine("<hr style='border:0; border-top:0.5px solid #999; width:100%;' />");
            sb.AppendLine($"<b>From:</b> {FormatEmailAddress(message.FromName, message.FromEmail)}<br/>");
            sb.AppendLine($"<b>Sent:</b> {message.Date:dddd, MMMM d, yyyy h:mm tt}<br/>");
            sb.AppendLine($"<b>To:</b> {FormatEmailAddress(message.ToName, message.ToEmail)}<br/>");
            sb.AppendLine($"<b>Subject:</b> {message.Subject}<br/><br/>");
            sb.AppendLine($"{message.Body}<br/><br/>");
        }

        return sb.ToString();
    }

    private static string FormatEmailAddress(string? name, string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return name ?? "";

        if (string.IsNullOrWhiteSpace(name))
            return $"&lt;{email}&gt;";

        return $"{name} &lt;{email}&gt;";
    }

    private class EmailThreadMessage
    {
        public string? MessageId { get; set; }
        public string? FromName { get; set; }
        public string? FromEmail { get; set; }
        public string? ToName { get; set; }
        public string? ToEmail { get; set; }
        public string? Subject { get; set; }
        public string? Body { get; set; }
        public DateTime? Date { get; set; }
    }


    public async Task<string> AddUnsubscribedAsync(int clientId, string email)
    {
        var existing = await _context.UnsubscribedContacts
            .FirstOrDefaultAsync(x => x.ClientId == clientId && x.Email == email);

        if (existing != null)
            return "Already Unsubscribed";

        var item = new UnsubscribedContacts
        {
            ClientId = clientId,
            Email = email,
            CreatedAt = DateTime.UtcNow
        };

        _context.UnsubscribedContacts.Add(item);
        await _context.SaveChangesAsync();

        return "Unsubscribed Added Successfully";
    }

    public async Task<ContactEmailTimelineDto?> GetEmailTimeline(int contactId)
    {
        var contact = await _context.contacts
            .AsNoTracking()
            .Where(x => x.id == contactId)
            .Select(x => new
            {
                x.id,
                x.full_name,
                x.email,
                x.created_at
            })
            .FirstOrDefaultAsync();

        if (contact == null)
            return null;

        // =========================
        // SENT EMAILS
        // =========================
        var emailLogs = await _context.EmailLogs
            .AsNoTracking()
            .Where(x => x.ContactId == contactId && x.TrackingId != null)
            .OrderBy(x => x.SentAt)
            .ToListAsync();

        var Inboxemails = await _context.InboxEmails
            .AsNoTracking()
            .Where(x => x.Contactid == contactId && x.TrackingId != null)
            .OrderBy(x => x.CreatedAt)
            .ToListAsync();

        var trackingIds = emailLogs
            .Where(x => x.TrackingId != null)
            .Select(x => x.TrackingId)
            .Distinct()
            .ToList();

        var messageIds = emailLogs
            .Where(x => !string.IsNullOrEmpty(x.MessageId))
            .Select(x => x.MessageId)
            .Distinct()
            .ToList();
        var inboxTrackingIds = Inboxemails
            .Where(x => x.TrackingId != null)
            .Select(x => x.TrackingId)
            .Distinct()
            .ToList();
        // =========================
        // TRACKING EVENTS
        // =========================
        var trackingEvents = await _context.EmailTrackingLogs
            .AsNoTracking()
            .Where(x =>
                trackingIds.Contains(x.TrackingId) &&
                (x.EventType == "OPEN" || x.EventType == "CLICK"))
            .ToListAsync();

        // =========================
        // REPLIES
        // =========================
        var replies = await _context.EmailReplies
             .AsNoTracking()
             .Where(x =>
                 x.ContactId == contactId)
             .OrderBy(x => x.Date)
             .ToListAsync();

        // =========================
        // NOTES
        // =========================
        var notes = await _context.Notes
            .AsNoTracking()
            .Where(x => x.ContactId == contactId)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new ContactNoteDto
            {
                Id = x.Id,
                Note = x.Note,
                CreatedAt = x.CreatedAt,
                IsPin = x.IsPin,
                IsUseInGenration = x.IsUseInGenration
            })
            .ToListAsync();

        // =========================
        // ATTACHMENTS
        // =========================
        var attachments = await _context.ContactAttachments
            .AsNoTracking()
            .Where(x => x.ContactId == contactId)
            .OrderByDescending(x => x.CreatedDate)
            .Select(x => new ContactAttachmentDto
            {
                Id = x.Id,
                FileName = x.FileName,
                FileUrl = x.FileUrl,
                CreatedDate = x.CreatedDate
            })
            .ToListAsync();
        // =========================
        // BUILD EMAIL TIMELINE
        // =========================
        var emailMessageIds = emailLogs
            .Select(x => x.MessageId)

            .Union(Inboxemails.Select(x => x.MessageId))

            .Union(replies.Select(x => x.MessageId))

            .Where(x => !string.IsNullOrWhiteSpace(x))

            .Distinct()
            .ToList();

        var allTrackingIds = emailLogs
             .Where(x => x.TrackingId != null)
             .Select(x => x.TrackingId)

             .Union(
                 Inboxemails
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

        var repliesWithoutTracking = replies
            .Where(x => x.TrackingId == null)
            .ToList();

        var emailAttachments = await _context.EmailAttachments
            .AsNoTracking()
            .Where(x => emailMessageIds.Contains(x.MessageId))
            .ToListAsync();

        var conversations = allTrackingIds
            .Select(trackingId =>
            {
                var sentGroup = emailLogs
                    .Where(x => x.TrackingId == trackingId)
                    .ToList();

                var replyGroup = replies
                    .Where(x => x.TrackingId == trackingId)
                    .ToList();

                var inboxGroup = Inboxemails
                    .Where(x => x.TrackingId == trackingId)
                    .ToList();

                var messages = new List<EmailConvDto>();

                messages.AddRange(
                inboxGroup
                    .Where(i =>
                        !replyGroup.Any(r => r.MessageId == i.MessageId)
                    )
                    .Select(i => new EmailConvDto
                    {
                        Type = "Inbox",
                        MessageId = i.MessageId,
                        Subject = i.Subject,
                        Body = i.Body,
                        FromEmail = i.FromEmail,
                        ToEmail = contact.email,
                        Date = i.Date,
                        IsRead = i.IsRead,
                        ContactId = i.Contactid,
                        ContactName = i.FromName,
                        Attachments = emailAttachments
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
                        ContactId = s.ContactId,
                        ContactName = s.EmailSenderName,
                        Attachments = emailAttachments
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
                    replyGroup.Select(r => new EmailConvDto
                    {
                        Type = "Reply",
                        MessageId = r.MessageId,
                        Subject = r.Subject,
                        Body = r.Body,
                        FromEmail = r.FromEmail,
                        ToEmail = contact.email,
                        Date = r.Date,
                        IsRead = r.IsRead ?? false,
                        ContactId = r.ContactId,
                        ContactName = r.FromEmail,
                        Attachments = emailAttachments
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

                    Subject =
                        inboxGroup.FirstOrDefault()?.Subject
                        ??
                        sentGroup.FirstOrDefault()?.Subject
                        ??
                        replyGroup.FirstOrDefault()?.Subject,

                    ContactEmail = contact.email,

                    ContactId = contactId,

                    TotalMessages = messages.Count,

                    LastMessageDate = messages.Max(x => x.Date),

                    HasUnread = messages.Any(x =>
                        (x.Type == "Inbox" || x.Type == "Reply")
                        && !x.IsRead),

                    IsPinned = false,

                    Messages = messages
                };
            })
            .Where(x => x != null)
            .OrderByDescending(x => x.LastMessageDate)
            .ToList();
        // =========================
        // FINAL RETURN
        // =========================
        return new ContactEmailTimelineDto
        {
            ContactId = contact.id,
            FullName = contact.full_name,
            Email = contact.email,
            ContactCreatedAt = contact.created_at,
            Conversations = conversations,
            Notes = notes,
            Attachments = attachments
        };
    }
    public async Task<object> AddContactsToSegmentAsync(int clientId, int segmentId, List<int> contactIds)
    {
        if (contactIds == null || !contactIds.Any())
            throw new ArgumentException("ContactIds cannot be empty");

        // Validate segment
        bool segmentExists = await _context.segments
            .AnyAsync(s => s.Id == segmentId && s.ClientId == clientId);

        if (!segmentExists)
            throw new Exception("Invalid SegmentId or ClientId");

        var requestedContactIds = contactIds.Distinct().ToList();

        // Valid contacts
        var validContactIds = await _context.contacts
            .Where(c => requestedContactIds.Contains(c.id))
            .Select(c => c.id)
            .ToListAsync();

        var invalidContactIds = requestedContactIds.Except(validContactIds).ToList();

        if (!validContactIds.Any())
        {
            return new
            {
                message = "None of the provided contacts exist",
                invalidContactIds
            };
        }

        // Already added
        var alreadyAddedContactIds = await _context.segmentContacts
            .Where(sc => sc.SegmentId == segmentId
                      && validContactIds.Contains(sc.ContactId))
            .Select(sc => sc.ContactId)
            .ToListAsync();

        // New contacts
        var newContactIds = validContactIds.Except(alreadyAddedContactIds).ToList();

        if (!newContactIds.Any())
        {
            return new
            {
                message = "All valid contacts already exist in the segment",
                alreadyPresentCount = alreadyAddedContactIds.Count
            };
        }

        var segmentContacts = newContactIds.Select(contactId => new SegmentContact
        {
            SegmentId = segmentId,
            ContactId = contactId,
            AddedAt = DateTime.UtcNow
        }).ToList();

        _context.segmentContacts.AddRange(segmentContacts);
        await _context.SaveChangesAsync();

        return new
        {
            message = "Contacts added to existing segment successfully",
            segmentId,
            contactsRequested = requestedContactIds.Count,
            contactsAdded = newContactIds.Count,
            alreadyPresentCount = alreadyAddedContactIds.Count,
            invalidContactCount = invalidContactIds.Count,
            invalidContactIds
        };
    }
    public async Task<FullTrackingDataResponse> GetFullTrackingData(int clientId, int dataFileId)
    {
        var contacts = await _context.contacts
            .Where(c => c.DataFileId == dataFileId)
            .ToListAsync();

        var trackingLogs = await _context.EmailTrackingLogs
            .Where(t => t.ClientId == clientId && t.DataFileId == dataFileId)
            .ToListAsync();

        var emailLogs = await _context.EmailLogs
            .Where(e => e.ClientId == clientId && e.DataFileId == dataFileId)
            .ToListAsync();

        return new FullTrackingDataResponse
        {
            Contacts = contacts,
            EmailTrackingLogs = trackingLogs,
            EmailLogs = emailLogs
        };
    }

    public async Task<ContactColumnResponseDto> GetContactColumnsWithCustomFields(int clientId)
    {
        // ❌ Fields jo bulk update ke liye allowed nahi hain
        var restrictedFields = new List<string>
        {
            "id",
            "data_file_id",
            "email",
            "full_name",
            "linkedin_url",
            "created_at",
            "updated_at",
            "email_sent_at",
            "data_file",
            "DataFileId",
            "linkedIninformation"
        };

        // ✅ Sirf valid fields hi lo
        var contactColumns = typeof(Contact)
            .GetProperties()
            .Select(p => p.Name)
            .Where(name => !restrictedFields.Contains(name))
            .ToList();

        // ✅ FULL custom fields data
        var customFields = await _context.crm_custom_fields
            .Where(x => x.client_id == clientId)
            .Select(x => new CustomFieldFullDto
            {
                Id = x.id,
                client_id = x.client_id,
                field_name = x.field_name,
                field_key = x.field_key,
                field_type = x.field_type,
                options_json = x.options_json
            })
            .ToListAsync();

        return new ContactColumnResponseDto
        {
            ContactColumns = contactColumns,
            CustomFields = customFields
        };
    }

    public async Task<bool> BulkUpdateFieldAsync(BulkUpdateFieldDto dto)
    {
        var strategy = _context.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _context.Database.BeginTransactionAsync();

            try
            {
                if (!dto.IsCustom)
                {
                    // ✅ NORMAL FIELD UPDATE
                    var contacts = await _context.contacts
                        .Where(x => dto.ContactIds.Contains(x.id))
                        .ToListAsync();

                    foreach (var contact in contacts)
                    {
                        var prop = typeof(Contact).GetProperty(dto.FieldName);

                        if (prop != null && prop.CanWrite)
                        {
                            var targetType = Nullable.GetUnderlyingType(prop.PropertyType)
                                             ?? prop.PropertyType;

                            var safeValue = dto.Value == null
                                ? null
                                : Convert.ChangeType(dto.Value, targetType);

                            prop.SetValue(contact, safeValue);
                        }
                    }
                }
                else
                {
                    // ✅ CUSTOM FIELD UPDATE / CREATE
                    var existingValues = await _context.contact_custom_field_values
                        .Where(x =>
                            dto.ContactIds.Contains(x.contact_id) &&
                            x.field_id == dto.FieldId)
                        .ToListAsync();

                    var existingContactIds = existingValues
                        .Select(x => x.contact_id)
                        .ToHashSet();

                    // 🔁 UPDATE existing
                    foreach (var item in existingValues)
                    {
                        item.value = dto.Value;
                        item.created_at = DateTime.UtcNow;
                    }

                    // ➕ CREATE missing
                    var missingContactIds = dto.ContactIds
                        .Where(id => !existingContactIds.Contains(id));

                    foreach (var contactId in missingContactIds)
                    {
                        _context.contact_custom_field_values.Add(
                            new ContactCustomFieldValue
                            {
                                contact_id = contactId,
                                field_id = dto.FieldId.Value,
                                value = dto.Value,
                                created_at = DateTime.UtcNow
                            });
                    }
                }

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                return true;
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        });
    }

    // Every email that belongs to a contact — what we sent (EmailLogs), the
    // replies that came back (EmailReplies) and everything the mailbox sync
    // picked up (InboxEmails) — merged into one chronological conversation.
    // Messages are matched by contact id OR by the contact's address, then the
    // match is expanded along tracking id / thread id / In-Reply-To links so a
    // message that was stored without a contact id (thread replies, forwards,
    // externally started threads) still travels with the rest of its thread.
    public async Task<ContactEmailConversationContextDto?> GetEmailConversationContextAsync(int clientId, int contactId)
    {
        Log.Information("Step 1: Fetch contact. ClientId={ClientId}, ContactId={ContactId}", clientId, contactId);

        var contact = await _context.contacts
            .AsNoTracking()
            .Where(x => x.id == contactId)
            .Select(x => new
            {
                x.id,
                x.full_name,
                x.email,
                x.created_at
            })
            .FirstOrDefaultAsync();

        if (contact == null)
        {
            Log.Information("Contact not found. ContactId={ContactId}", contactId);
            return null;
        }

        var contactEmail = (contact.email ?? string.Empty).Trim().ToLower();
        var hasContactEmail = !string.IsNullOrWhiteSpace(contactEmail);

        // ---------------------------------------------------------------
        // Step 2: seeds — anything already tied to this contact
        // ---------------------------------------------------------------
        var sentLogs = await _context.EmailLogs
            .AsNoTracking()
            .Where(x =>
                x.ClientId == clientId &&
                x.IsSuccess &&
                !x.IsDeleted &&
                (
                    x.ContactId == contactId ||
                    (hasContactEmail && x.ToEmail != null && x.ToEmail.ToLower() == contactEmail)
                ))
            .ToListAsync();

        var replies = await _context.EmailReplies
            .AsNoTracking()
            .Where(x =>
                x.ClientId == clientId &&
                (x.IsDeleted == null || x.IsDeleted == false) &&
                (
                    x.ContactId == contactId ||
                    (hasContactEmail && x.FromEmail != null && x.FromEmail.ToLower() == contactEmail) ||
                    (hasContactEmail && x.ToEmail != null && x.ToEmail.ToLower() == contactEmail)
                ))
            .ToListAsync();

        var inboxEmails = await _context.InboxEmails
            .AsNoTracking()
            .Where(x =>
                x.ClientId == clientId &&
                !x.IsDeleted &&
                (
                    x.Contactid == contactId ||
                    (hasContactEmail && x.FromEmail != null && x.FromEmail.ToLower() == contactEmail) ||
                    (hasContactEmail && x.ToEmail != null && x.ToEmail.ToLower() == contactEmail)
                ))
            .ToListAsync();

        Log.Information(
            "Seeds — sent: {Sent}, replies: {Replies}, inbox: {Inbox}",
            sentLogs.Count, replies.Count, inboxEmails.Count);

        // ---------------------------------------------------------------
        // Step 3: expand along thread links
        // ---------------------------------------------------------------
        var trackingIds = new HashSet<Guid>();
        var threadIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var messageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void CollectKeys(Guid? trackingId, string? threadId, params string?[] ids)
        {
            if (trackingId.HasValue && trackingId.Value != Guid.Empty)
                trackingIds.Add(trackingId.Value);

            if (!string.IsNullOrWhiteSpace(threadId))
                threadIds.Add(threadId.Trim());

            foreach (var id in ids)
                foreach (var variant in MessageIdVariants(id))
                    messageIds.Add(variant);
        }

        foreach (var log in sentLogs)
            CollectKeys(log.TrackingId, log.ThreadId, log.MessageId);

        foreach (var reply in replies)
            CollectKeys(reply.TrackingId, reply.ThreadId, reply.MessageId, reply.InReplyTo);

        foreach (var inbox in inboxEmails)
            CollectKeys(inbox.TrackingId, inbox.ThreadId, inbox.MessageId, inbox.InReplyTo);

        var trackingIdList = trackingIds.ToList();
        var threadIdList = threadIds.ToList();
        var messageIdList = messageIds.ToList();

        if (trackingIdList.Any() || threadIdList.Any() || messageIdList.Any())
        {
            var knownLogIds = sentLogs.Select(x => x.Id).ToHashSet();
            var knownReplyIds = replies.Select(x => x.Id).ToHashSet();
            var knownInboxIds = inboxEmails.Select(x => x.Id).ToHashSet();

            var extraLogs = await _context.EmailLogs
                .AsNoTracking()
                .Where(x =>
                    x.ClientId == clientId &&
                    x.IsSuccess &&
                    !x.IsDeleted &&
                    (
                        (x.TrackingId.HasValue && trackingIdList.Contains(x.TrackingId.Value)) ||
                        (x.ThreadId != null && threadIdList.Contains(x.ThreadId)) ||
                        (x.MessageId != null && messageIdList.Contains(x.MessageId))
                    ))
                .ToListAsync();

            var extraReplies = await _context.EmailReplies
                .AsNoTracking()
                .Where(x =>
                    x.ClientId == clientId &&
                    (x.IsDeleted == null || x.IsDeleted == false) &&
                    (
                        (x.TrackingId.HasValue && trackingIdList.Contains(x.TrackingId.Value)) ||
                        (x.ThreadId != null && threadIdList.Contains(x.ThreadId)) ||
                        (x.MessageId != null && messageIdList.Contains(x.MessageId)) ||
                        (x.InReplyTo != null && messageIdList.Contains(x.InReplyTo))
                    ))
                .ToListAsync();

            var extraInbox = await _context.InboxEmails
                .AsNoTracking()
                .Where(x =>
                    x.ClientId == clientId &&
                    !x.IsDeleted &&
                    (
                        (x.TrackingId.HasValue && trackingIdList.Contains(x.TrackingId.Value)) ||
                        (x.ThreadId != null && threadIdList.Contains(x.ThreadId)) ||
                        (x.MessageId != null && messageIdList.Contains(x.MessageId)) ||
                        (x.InReplyTo != null && messageIdList.Contains(x.InReplyTo))
                    ))
                .ToListAsync();

            sentLogs.AddRange(extraLogs.Where(x => !knownLogIds.Contains(x.Id)));
            replies.AddRange(extraReplies.Where(x => !knownReplyIds.Contains(x.Id)));
            inboxEmails.AddRange(extraInbox.Where(x => !knownInboxIds.Contains(x.Id)));
        }

        Log.Information(
            "After expansion — sent: {Sent}, replies: {Replies}, inbox: {Inbox}",
            sentLogs.Count, replies.Count, inboxEmails.Count);

        // ---------------------------------------------------------------
        // Step 4: merge into one de-duplicated, chronological conversation
        // ---------------------------------------------------------------
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var messages = new List<ConversationEmailDto>();

        void AddMessage(ConversationEmailDto message)
        {
            var keys = new List<string>(2);

            var idKey = NormalizeMessageId(message.MessageId);
            if (!string.IsNullOrEmpty(idKey))
                keys.Add("id:" + idKey);

            // The same mail can be stored twice under different ids — the send
            // keeps the provider's id while the mailbox sync keeps the RFC
            // Message-Id header — so also key it by its content.
            var subject = (message.Subject ?? "").Trim().ToLower();
            var body = (message.Body ?? "").Trim();

            if (subject.Length > 0 || body.Length > 0)
            {
                keys.Add("content:" + string.Join("|",
                    message.Direction,
                    subject,
                    message.SentAt?.ToString("yyyyMMddHHmm") ?? "",
                    body.Length > 200 ? body[..200] : body));
            }

            if (!keys.Any())
                keys.Add("row:" + message.Source + ":" + message.SourceId);

            if (keys.Any(seen.Contains))
                return;

            foreach (var key in keys)
                seen.Add(key);

            messages.Add(message);
        }

        // Our own sends win over a mailbox copy of the same mail: they carry
        // the campaign/blueprint metadata and the body we actually generated.
        foreach (var log in sentLogs)
        {
            AddMessage(new ConversationEmailDto
            {
                EmailLogId = log.Id,
                Source = "EmailLog",
                SourceId = log.Id,
                Direction = "Sent",
                MessageId = log.MessageId,
                ThreadId = log.ThreadId,
                TrackingId = log.TrackingId,
                SentAt = log.SentAt,
                SenderName = log.EmailSenderName,
                SenderEmailId = log.SenderEmailId,
                RecipientName = log.EmailRecipientName,
                ToEmail = log.ToEmail,
                Subject = log.Subject,
                Body = log.Body
            });
        }

        foreach (var reply in replies)
        {
            AddMessage(new ConversationEmailDto
            {
                Source = "EmailReply",
                SourceId = reply.Id,
                Direction = DirectionFor(reply.FromEmail, "Received"),
                MessageId = reply.MessageId,
                InReplyTo = reply.InReplyTo,
                ThreadId = reply.ThreadId,
                TrackingId = reply.TrackingId,
                SentAt = reply.Date,
                SenderName = reply.FromName,
                SenderEmailId = reply.FromEmail,
                ToEmail = reply.ToEmail,
                Subject = reply.Subject,
                Body = reply.Body
            });
        }

        foreach (var inbox in inboxEmails)
        {
            AddMessage(new ConversationEmailDto
            {
                Source = "InboxEmail",
                SourceId = inbox.Id,
                Direction = DirectionFor(inbox.FromEmail, "Received"),
                MessageId = inbox.MessageId,
                InReplyTo = inbox.InReplyTo,
                ThreadId = inbox.ThreadId,
                TrackingId = inbox.TrackingId,
                SentAt = inbox.Date,
                SenderName = inbox.FromName,
                SenderEmailId = inbox.FromEmail,
                ToEmail = inbox.ToEmail,
                Subject = inbox.Subject,
                Body = inbox.Body
            });
        }

        messages = messages
            .OrderBy(x => x.SentAt ?? DateTime.MaxValue)
            .ThenBy(x => x.SourceId)
            .ToList();

        // Old shape: hang every inbound message off the outbound one it answers.
        var inboundMessages = messages.Where(x => x.Direction == "Received").ToList();

        foreach (var sent in messages.Where(x => x.Direction == "Sent"))
        {
            var sentMessageId = NormalizeMessageId(sent.MessageId);

            sent.Replies = inboundMessages
                .Where(r =>
                    (sent.TrackingId.HasValue && r.TrackingId == sent.TrackingId) ||
                    (!string.IsNullOrEmpty(sentMessageId) &&
                     NormalizeMessageId(r.InReplyTo) == sentMessageId))
                .Select(r => new EmailReplyDto
                {
                    Id = (int)r.SourceId,
                    MessageId = r.MessageId,
                    InReplyTo = r.InReplyTo,
                    FromEmail = r.SenderEmailId,
                    Subject = r.Subject,
                    Body = r.Body,
                    Date = r.SentAt,
                    IsRead = true
                })
                .ToList();
        }

        Log.Information("Step 5: Build response. Messages={Count}", messages.Count);

        return new ContactEmailConversationContextDto
        {
            ClientId = clientId,
            ContactId = contact.id,
            FullName = contact.full_name,
            Email = contact.email,
            ContactCreatedAt = contact.created_at,
            Emails = messages,
            TotalCount = messages.Count,
            SentCount = messages.Count(x => x.Direction == "Sent"),
            ReceivedCount = messages.Count(x => x.Direction == "Received"),
            PromptContext = BuildPromptContext(contact.full_name, contact.email, contact.created_at, messages)
        };

        // A mailbox-synced message is inbound when it comes from the contact;
        // anything else on the thread is one of ours that the sync pulled back.
        string DirectionFor(string? fromEmail, string fallback)
        {
            if (!hasContactEmail)
                return fallback;

            return (fromEmail ?? "").Trim().ToLower() == contactEmail
                ? "Received"
                : "Sent";
        }
    }

    // Message-Ids are stored with and without angle brackets depending on the
    // provider, so compare on the bare value.
    private static string NormalizeMessageId(string? messageId)
        => string.IsNullOrWhiteSpace(messageId)
            ? ""
            : messageId.Trim().Trim('<', '>').Trim().ToLowerInvariant();

    private static IEnumerable<string> MessageIdVariants(string? messageId)
    {
        var bare = NormalizeMessageId(messageId);

        if (string.IsNullOrEmpty(bare))
            yield break;

        yield return bare;
        yield return $"<{bare}>";
    }

    public async Task<InboxContactSaveDTO> SaveConversationContactAsync(string fullName, string email, int clientId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(email))
            {
                return new InboxContactSaveDTO
                {
                    Success = false,
                    Message = "Email address required"
                };
            }

            email = email.Trim().ToLower();

            var existingContact = await _context.contacts
                .Include(x => x.data_file)
                .FirstOrDefaultAsync(x =>
                    x.email.ToLower() == email &&
                    x.data_file.client_id == clientId);

            if (existingContact != null)
            {
                return new InboxContactSaveDTO
                {
                    Success = true,
                    ContactId = existingContact.id,
                    Message = "Existing contact found"
                };
            }

            var dataFile = await _context.data_files
                .FirstOrDefaultAsync(x =>
                    x.client_id == clientId &&
                    x.name == "Contacts involved in conversations");

            if (dataFile == null)
            {
                dataFile = new DataFile
                {
                    client_id = clientId,
                    name = "Contacts involved in conversations",
                    data_file_name = "Contacts involved in conversations",
                    description = "Auto created for conversation contacts",
                    created_at = DateTime.UtcNow
                };

                _context.data_files.Add(dataFile);
                await _context.SaveChangesAsync();
            }

            var contact = new Contact
            {
                full_name = fullName,
                email = email,
                DataFileId = dataFile.id,
                created_at = DateTime.UtcNow
            };

            _context.contacts.Add(contact);
            await _context.SaveChangesAsync();

            return new InboxContactSaveDTO
            {
                Success = true,
                ContactId = contact.id,
                Message = "Contact created successfully"
            };
        }
        catch (Exception ex)
        {
            return new InboxContactSaveDTO
            {
                Success = false,
                Message = ex.Message
            };
        }
    }
    public async Task<OperationResult> CreateSignature(CreateEmailSignatureDto dto)
    {
        try
        {

            if (string.IsNullOrWhiteSpace(dto.SignatureName))
                return new OperationResult
                {
                    Success = false,
                    Message = "Signature name is required"
                };

            if (string.IsNullOrWhiteSpace(dto.SignatureHtml))
                return new OperationResult
                {
                    Success = false,
                    Message = "Signature content is required"
                };

            var signiture = await _context.EmailSignatures
                .FirstOrDefaultAsync(x =>
                    x.OutboxId == dto.OutboxId &&
                    x.Provider == dto.Provider &&
                    x.ClientId == dto.ClientId);

            if (signiture != null)

                return new OperationResult
                {
                    Success = false,
                    Message = "Signature already exist for this account"
                };

            if (string.Equals(dto.Provider, "SMTP", StringComparison.OrdinalIgnoreCase))
            {
                var smtpRecord = await _context.SmtpCredentials
                    .FirstOrDefaultAsync(x => x.Id == dto.OutboxId);

                if (smtpRecord == null)
                {
                    return new OperationResult
                    {
                        Success = false,
                        Message = "SMTP account not found."
                    };
                }
            }
            else
            {
                var oauthRecord = await _context.EmailOAuthTokens
                    .FirstOrDefaultAsync(x => x.Id == dto.OutboxId);

                if (oauthRecord == null)
                {
                    return new OperationResult
                    {
                        Success = false,
                        Message = "Email account not found."
                    };
                }
            }

            if (dto.IsDefault)
            {
                var existingDefaults = await _context.EmailSignatures
                    .Where(x => x.OutboxId == dto.OutboxId)
                    .ToListAsync();

                foreach (var item in existingDefaults)
                {
                    item.IsDefault = false;
                }
            }

            var signature = new EmailSignatures
            {
                ClientId = dto.ClientId,
                OutboxId = dto.OutboxId,
                SignatureName = dto.SignatureName,
                SignatureHtml = dto.SignatureHtml,
                Provider = dto.Provider,
                IsDefault = dto.IsDefault,
                CreatedAt = DateTime.UtcNow
            };

            _context.EmailSignatures.Add(signature);

            await _context.SaveChangesAsync();

            return new OperationResult
            {
                Success = true,
                Message = "Signature created successfully"
            };
        }
        catch (Exception ex)
        {
            return new OperationResult
            {
                Success = false,
                Message = ex.Message
            };
        }
    }
    public async Task<List<EmailAccountDto>> GetEmailAccounts(int clientId)
    {
        var oauthAccounts = await _context.EmailOAuthTokens
            .Where(x => x.ClientId == clientId)
            .Select(x => new EmailAccountDto
            {
                Id = x.Id,
                Email = x.Email,
                Provider = x.Provider,
                SenderName = x.SenderName,
                FullInboxSync = x.FullInboxSync,
                CreatedAt = x.CreatedAt
            })
            .ToListAsync();

        string clientIdInt = clientId.ToString();

        var smtpAccounts = await _context.SmtpCredentials
            .Where(x => x.ClientId == clientIdInt)
            .Select(x => new EmailAccountDto
            {
                Id = x.Id,
                Email = x.FromEmail,
                Provider = "SMTP",
                SenderName = x.SenderName
            })
            .ToListAsync();

        return oauthAccounts
            .Concat(smtpAccounts)
            .OrderBy(x => x.Email)
            .ToList();
    }

    public async Task<List<EmailSignatures>> GetSignatures(int clientId)
    {
        return await _context.EmailSignatures
            .Where(x => x.ClientId == clientId)
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync();
    }

    public async Task<EmailSignatures> GetSingleSignatures(int clientId, int InboxId, string Provider, string Mathod)
    {
        int? outboxId = 0;

        // =========================
        // OUTBOX RESOLVE
        // =========================
        if(Mathod == "Inbox") 
        {
            if (Provider.ToUpper() == "IMAP")
            {
                outboxId = await _context.Inboxcredentials
                    .Where(x => x.Id == InboxId)
                    .Select(x => x.Outboxid)
                    .FirstOrDefaultAsync();
            }
            else if (Provider.ToUpper() == "GMAIL" || Provider.ToUpper() == "OUTLOOK")
            {
                outboxId = InboxId;
            }
        }
        else
        {
            if (Provider.ToUpper() == "IMAP")
            {
                outboxId = InboxId;
            }
            else if (Provider.ToUpper() == "GMAIL" || Provider.ToUpper() == "OUTLOOK")
            {
                outboxId = InboxId;
            }
        }
        

        return await _context.EmailSignatures.FirstOrDefaultAsync(x => x.ClientId == clientId && x.OutboxId == outboxId);
            
    }
    public async Task<OperationResult> UpdateSignature(UpdateEmailSignatureDto model)
    {
        try
        {
            var signature = await _context.EmailSignatures
                .FirstOrDefaultAsync(x => x.Id == model.Id);

            if (signature == null)
            {
                return new OperationResult
                {
                    Success = false,
                    Message = "Signature not found"
                };
            }

            signature.SignatureName = model.SignatureName;
            signature.SignatureHtml = model.SignatureHtml;
            signature.IsDefault = model.IsDefault;
            signature.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            return new OperationResult
            {
                Success = true,
                Message = "Signature updated successfully"
            };
        }
        catch (Exception ex)
        {
            return new OperationResult
            {
                Success = false,
                Message = ex.Message
            };
        }
    }
    public Task<bool> CreditDeduction(int clientId) => CreditDeduction(clientId, 1);

    /// <summary>
    /// Deducts <paramref name="units"/> credits in one go.
    ///
    /// Most callers spend one credit per action, but a validation run is
    /// priced per batch — one credit per ten contacts — so the amount has to
    /// be variable. Deducting in a single call rather than looping keeps the
    /// balance consistent: a loop that fails halfway would leave the client
    /// charged for work that never ran.
    ///
    /// All-or-nothing. If the plan cannot cover the full amount, nothing is
    /// deducted and the caller is told to stop rather than half-charged.
    /// </summary>
    public async Task<bool> CreditDeduction(int clientId, int units)
    {
        if (units <= 0)
            return true;

        var finalCredit = await _context.FinalUserCredit
            .FirstOrDefaultAsync(f => f.ClientId == clientId);

        if (finalCredit == null)
            return false;

        bool isDeducted = false;

        // Case 1: Use TotalCredit if available and monthly limit not reached
        if ((finalCredit.TotalCredit ?? 0) >= units &&
            (finalCredit.LimitUsed ?? 0) + units <= (finalCredit.MonthlyLimit ?? 0))
        {
            var activePlan = await _context.UserCredits
                .Where(u =>
                    u.ClientId == clientId &&
                    u.Status != null &&
                    u.Status.ToLower() == "active" &&
                    u.Plane != "Custom Credit" &&
                    u.Plane != "Internal" &&
                    (u.Credits ?? 0) >= units)
                .OrderByDescending(u => u.StartDate ?? u.CreatedAt)
                .FirstOrDefaultAsync();

            if (activePlan == null)
                return false;

            finalCredit.TotalCredit -= units;
            finalCredit.UsedCredit = (finalCredit.UsedCredit ?? 0) + units;
            finalCredit.LimitUsed = (finalCredit.LimitUsed ?? 0) + units;

            activePlan.Credits -= units;
            _context.UserCredits.Update(activePlan);

            isDeducted = true;
        }
        // Case 2: Use CustomLimit
        else if ((finalCredit.CustomLimit ?? 0) >= units)
        {
            var activePlan = await _context.UserCredits
                .FirstOrDefaultAsync(u =>
                    u.ClientId == clientId &&
                    u.Status.ToLower() == "active" &&
                    (u.Plane == "Custom Credit" || u.Plane == "Internal") &&
                    (u.Credits ?? 0) >= units);

            if (activePlan != null)
            {
                finalCredit.CustomLimit -= units;
                finalCredit.CustomCreditUsed = (finalCredit.CustomCreditUsed ?? 0) + units;

                activePlan.Credits -= units;

                _context.UserCredits.Update(activePlan);

                isDeducted = true;
            }
        }

        if (!isDeducted)
            return false;

        finalCredit.UpdatedAt = DateTime.UtcNow;
        _context.FinalUserCredit.Update(finalCredit);

        await _context.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// Puts credits back after a run did less work than it was charged for —
    /// a model that skips 12 of 50 contacts should not be paid for them.
    ///
    /// Mirrors <see cref="CreditDeduction(int, int)"/> in reverse, and is
    /// deliberately best-effort: a refund that cannot be applied is logged by
    /// the caller, never allowed to fail the job that earned it.
    /// </summary>
    public async Task<bool> CreditRefund(int clientId, int units)
    {
        if (units <= 0)
            return true;

        var finalCredit = await _context.FinalUserCredit
            .FirstOrDefaultAsync(f => f.ClientId == clientId);

        if (finalCredit == null)
            return false;

        // Refund to whichever bucket the deduction came out of. Custom credit
        // is only ever used once the monthly allowance is exhausted, so a
        // non-zero CustomCreditUsed is what marks that path.
        bool usedCustom = (finalCredit.CustomCreditUsed ?? 0) >= units &&
                          (finalCredit.UsedCredit ?? 0) < units;

        var activePlan = usedCustom
            ? await _context.UserCredits.FirstOrDefaultAsync(u =>
                u.ClientId == clientId &&
                u.Status != null &&
                u.Status.ToLower() == "active" &&
                (u.Plane == "Custom Credit" || u.Plane == "Internal"))
            : await _context.UserCredits
                .Where(u =>
                    u.ClientId == clientId &&
                    u.Status != null &&
                    u.Status.ToLower() == "active" &&
                    u.Plane != "Custom Credit" &&
                    u.Plane != "Internal")
                .OrderByDescending(u => u.StartDate ?? u.CreatedAt)
                .FirstOrDefaultAsync();

        if (activePlan == null)
            return false;

        if (usedCustom)
        {
            finalCredit.CustomLimit = (finalCredit.CustomLimit ?? 0) + units;
            finalCredit.CustomCreditUsed = Math.Max(0, (finalCredit.CustomCreditUsed ?? 0) - units);
        }
        else
        {
            finalCredit.TotalCredit = (finalCredit.TotalCredit ?? 0) + units;
            finalCredit.UsedCredit = Math.Max(0, (finalCredit.UsedCredit ?? 0) - units);
            finalCredit.LimitUsed = Math.Max(0, (finalCredit.LimitUsed ?? 0) - units);
        }

        activePlan.Credits = (activePlan.Credits ?? 0) + units;
        _context.UserCredits.Update(activePlan);

        finalCredit.UpdatedAt = DateTime.UtcNow;
        _context.FinalUserCredit.Update(finalCredit);

        await _context.SaveChangesAsync();
        return true;
    }

    public async Task<OperationResult> DeleteSignature(int id, int clientId)
    {
        try
        {
            var signature = await _context.EmailSignatures
                .FirstOrDefaultAsync(x => x.Id == id && x.ClientId == clientId);

            if (signature == null)
            {
                return new OperationResult
                {
                    Success = false,
                    Message = "Signature not found"
                };
            }

            _context.EmailSignatures.Remove(signature);

            await _context.SaveChangesAsync();

            return new OperationResult
            {
                Success = true,
                Message = "Signature deleted successfully"
            };
        }
        catch (Exception ex)
        {
            return new OperationResult
            {
                Success = false,
                Message = ex.Message
            };
        }
    }
    public async Task SaveKraftHistoryAsync(int contactId, int clientId, int? campaignId, int? blueprintId,string Process)
    {
        var history = new KraftHistory
        {
            ContactId = contactId,
            ClientId = clientId,
            CampaignId = campaignId,
            BlueprintId = blueprintId,
            Process = Process,
            KraftedDate = DateTime.UtcNow
        };

        _context.KraftHistory.Add(history);
        await _context.SaveChangesAsync();
    }


    //-------------------------------------------------------------------------------------private---------------------------------------------------------------------------------------------------------------
    private string? GetSourceName(EmailLog log)
    {
        if (log.DataFileId != null)
        {
            return _context.data_files
                .Where(x => x.id == log.DataFileId)
                .Select(x => x.name)
                .FirstOrDefault();
        }

        if (log.SegmentId != null)
        {
            return _context.segments
                .Where(x => x.Id == log.SegmentId)
                .Select(x => x.Name)
                .FirstOrDefault();
        }

        return null;
    }


    // One flat, chronological transcript — every message, inbound and
    // outbound, labelled with who sent it. Bodies are HTML-stripped so the
    // model reads the text and not the markup.
    // The transcript that gets embedded in a generation prompt. Bodies go
    // through PromptTextCleaner, which drops the markup, the footers and the
    // quoted trail — that trail is every earlier message repeated inside the
    // later ones, and they are already listed here in their own right.
    private const int PromptContextMessageLimit = 15;

    private static string BuildPromptContext(string? fullName, string? email, DateTime? contactCreatedAt, List<ConversationEmailDto> emails)
    {
        if (emails == null || !emails.Any())
            return string.Empty;

        var sentCount = emails.Count(x => x.Direction == "Sent");
        var receivedCount = emails.Count - sentCount;

        // Oldest messages are the least useful and the most likely to blow the
        // context budget, so only the most recent ones are written out in full.
        var omitted = Math.Max(0, emails.Count - PromptContextMessageLimit);
        var included = omitted == 0
            ? emails
            : emails.Skip(omitted).ToList();

        var sb = new StringBuilder();

        sb.AppendLine("PAST EMAIL CONVERSATION");
        sb.AppendLine($"Contact: {fullName} <{email}>");

        if (contactCreatedAt.HasValue)
            sb.AppendLine($"Contact Created At: {contactCreatedAt.Value:dddd, MMMM d, yyyy h:mm tt}");

        sb.AppendLine($"Total messages: {emails.Count} ({sentCount} sent by us, {receivedCount} received from the contact)");

        if (omitted > 0)
            sb.AppendLine($"Showing the {included.Count} most recent messages; {omitted} older message(s) omitted.");

        sb.AppendLine();

        for (int i = 0; i < included.Count; i++)
        {
            var item = included[i];

            var direction = item.Direction == "Sent"
                ? "SENT BY US"
                : "RECEIVED FROM CONTACT";

            var body = PromptTextCleaner.CleanEmailBody(item.Body);

            sb.AppendLine($"Message #{omitted + i + 1} - {direction}");
            sb.AppendLine($"Date: {item.SentAt:dddd, MMMM d, yyyy h:mm tt}");
            sb.AppendLine($"From: {FormatParticipant(item.SenderName, item.SenderEmailId)}");
            sb.AppendLine($"To: {FormatParticipant(item.RecipientName, item.ToEmail)}");
            sb.AppendLine($"Subject: {PromptTextCleaner.StripHtml(item.Subject)}");
            sb.AppendLine("Body:");
            sb.AppendLine(string.IsNullOrWhiteSpace(body) ? "(no readable text)" : body);
            sb.AppendLine();
            sb.AppendLine("--------------------------------------------------");
        }

        return sb.ToString();
    }

    private static string FormatParticipant(string? name, string? address)
    {
        var hasName = !string.IsNullOrWhiteSpace(name);
        var hasAddress = !string.IsNullOrWhiteSpace(address);

        if (hasName && hasAddress)
            return $"{name.Trim()} <{address.Trim()}>";

        if (hasAddress)
            return address.Trim();

        return hasName ? name.Trim() : "(unknown)";
    }

    // Text cleaning for prompts lives in PromptTextCleaner so the repository
    // and the generation controllers all treat stored HTML the same way.

    //-----------------------------------------------------------------------------




}

