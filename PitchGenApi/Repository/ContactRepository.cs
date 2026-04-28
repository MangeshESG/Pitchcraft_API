using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using PitchGenApi.Database;
using PitchGenApi.Model;
using PitchGenApi.Model.DTOs;
using PitchGenApi.Models;
using Serilog;
using System.Text;
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

    public async Task<bool> CreditDeduction(int clientId)
    {
        var finalCredit = await _context.FinalUserCredit
            .FirstOrDefaultAsync(f => f.ClientId == clientId);

        if (finalCredit == null)
            return false;

        bool isDeducted = false;

        // Case 1: Use TotalCredit if available and monthly limit not reached
        if ((finalCredit.TotalCredit ?? 0) > 0 &&
            (finalCredit.LimitUsed ?? 0) < (finalCredit.MonthlyLimit ?? 0))
        {
            finalCredit.TotalCredit -= 1;
            finalCredit.UsedCredit = (finalCredit.UsedCredit ?? 0) + 1;
            finalCredit.LimitUsed = (finalCredit.LimitUsed ?? 0) + 1;

            isDeducted = true;
        }
        // Case 2: Use CustomLimit
        else if ((finalCredit.CustomLimit ?? 0) > 0)
        {
            finalCredit.CustomLimit -= 1;
            finalCredit.CustomCreditUsed = (finalCredit.CustomCreditUsed ?? 0) + 1;

            var latestActivePlan = await _context.UserCredits
                .Where(u => u.ClientId == clientId &&
                            u.Status.ToLower() == "active" &&
                            u.Plane == "Custom Credit")
                .OrderByDescending(u => u.CreatedAt)
                .FirstOrDefaultAsync();

            if (latestActivePlan != null && latestActivePlan.Credits > 0)
            {
                latestActivePlan.Credits -= 1;
                _context.UserCredits.Update(latestActivePlan);
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

        if (!logs.Any())
            return "";

        StringBuilder sb = new StringBuilder();

        foreach (var log in logs)
        {
            sb.AppendLine("<hr style='border:0; border-top:0.5px solid #999; width:100%;' />");
            sb.AppendLine($"<b>From:</b> {log.EmailSenderName} &lt;{log.SenderEmailId}&gt;<br/>");
            sb.AppendLine($"<b>Sent:</b> {log.SentAt:dddd, MMMM d, yyyy h:mm tt}<br/>");
            sb.AppendLine($"<b>To:</b> {log.EmailRecipientName} &lt;{log.ToEmail}&gt;<br/>");
            sb.AppendLine($"<b>Subject:</b> {log.Subject}<br/><br/>");
            sb.AppendLine($"{log.Body}<br/><br/>");
        }

        return sb.ToString();
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
                x.ContactId == contactId &&
                (
                    (x.TrackingId != null && trackingIds.Contains(x.TrackingId))
                    ||
                    (x.InReplyTo != null && messageIds.Contains(x.InReplyTo))
                ))
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
        var emails = emailLogs
            .Select(log => new SentEmailDto
            {
                TrackingId = log.TrackingId?.ToString(),
                SentAt = log.SentAt,
                SenderEmailId = log.SenderEmailId,
                Subject = log.Subject,
                Body = log.Body,
                Source = GetSourceName(log),

                Events = trackingEvents
                    .Where(e => e.TrackingId == log.TrackingId)
                    .OrderBy(e => e.Timestamp)
                    .Select(e => new EmailEventDto
                    {
                        EventType = e.EventType,
                        EventAt = e.Timestamp,
                        TargetUrl = e.TargetUrl
                    })
                    .ToList(),

                Replies = replies
                    .Where(r =>
                        r.TrackingId == log.TrackingId ||
                        r.InReplyTo == log.MessageId)
                    .OrderBy(r => r.Date)
                    .Select(r => new EmailReplyDto
                    {
                        Id = r.Id,
                        MessageId = r.MessageId,
                        InReplyTo = r.InReplyTo,
                        FromEmail = r.FromEmail,
                        Subject = r.Subject,
                        Body = r.Body,
                        Date = r.Date,
                        IsRead = r.IsRead ?? false
                    })
                    .ToList()
            })
            .OrderBy(x => x.SentAt)
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
            Emails = emails,
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
        using var transaction = await _context.Database.BeginTransactionAsync();

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
                        prop.SetValue(contact, Convert.ChangeType(dto.Value, prop.PropertyType));
                    }
                }
            }
            else
            {
                // ✅ CUSTOM FIELD UPDATE / CREATE
                var existingValues = await _context.contact_custom_field_values
                    .Where(x => dto.ContactIds.Contains(x.contact_id) && x.field_id == dto.FieldId)
                    .ToListAsync();

                var existingContactIds = existingValues.Select(x => x.contact_id).ToList();

                // 🔁 UPDATE existing
                foreach (var item in existingValues)
                {
                    item.value = dto.Value;
                    item.created_at = DateTime.UtcNow;
                }

                // ➕ CREATE missing
                var missingContactIds = dto.ContactIds.Except(existingContactIds);

                foreach (var contactId in missingContactIds)
                {
                    _context.contact_custom_field_values.Add(new ContactCustomFieldValue
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

}

