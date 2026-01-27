using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using PitchGenApi.Database;
using PitchGenApi.Model;
using PitchGenApi.Model.DTOs;
using PitchGenApi.Models;
using Serilog;
using System.Text;

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
        var rows = await (
            from c in _context.contacts
            join el in _context.EmailLogs
                on c.id equals el.ContactId
            join et in _context.EmailTrackingLogs
                on el.TrackingId equals et.TrackingId into etg
            from et in etg.DefaultIfEmpty()
            where c.id == contactId
                  && el.TrackingId != null
                  && (et == null || et.EventType == "OPEN" || et.EventType == "CLICK")
            select new
            {
                Contact = c,
                EmailLog = el,
                Tracking = et
            }
        ).ToListAsync();

        if (!rows.Any())
            return null;

        var response = new ContactEmailTimelineDto
        {
            ContactId = contactId,
            FullName = rows.First().Contact.full_name,
            Email = rows.First().Contact.email,
            ContactCreatedAt = rows.First().Contact.created_at,

            Emails = rows
                .GroupBy(x => new
                {
                    x.EmailLog.TrackingId,
                    x.EmailLog.SentAt,
                    x.EmailLog.SenderEmailId,
                    x.EmailLog.Subject
                })
                .OrderBy(g => g.Key.SentAt)
                .Select(g =>
                {
                    var log = g.First().EmailLog;

                    return new SentEmailDto
                    {
                        TrackingId = g.Key.TrackingId.ToString(),
                        SentAt = g.Key.SentAt,
                        SenderEmailId = g.Key.SenderEmailId,
                        Subject = g.Key.Subject,
                        Body = log.Body,
                        Source = GetSourceName(log),

                        Events = g
                            .Where(x => x.Tracking != null)
                            .OrderBy(x => x.Tracking.Timestamp)
                            .Select(x => new EmailEventDto
                            {
                                EventType = x.Tracking.EventType,
                                EventAt = x.Tracking.Timestamp,
                                TargetUrl = x.Tracking.TargetUrl
                            })
                            .ToList()
                    };
                })
                .ToList()
        };

        return response;
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

