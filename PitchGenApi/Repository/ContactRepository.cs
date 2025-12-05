using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using PitchGenApi.Database;
using PitchGenApi.Model;
using PitchGenApi.Model.DTOs;
using PitchGenApi.Models;
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

    public async Task<string> BuildEmailThreadAsync(int clientId, int? datafileid, int contactid)
    {
        var logs = await _context.EmailLogs
            .Where(x => x.ClientId == clientId
                        && x.DataFileId == datafileid
                        && x.ContactId == contactid)   // <-- Contact-wise filter
            .OrderByDescending(x => x.SentAt)
            .ToListAsync();

        if (!logs.Any())
            return "";

        StringBuilder sb = new StringBuilder();

        foreach (var log in logs)
        {
            sb.AppendLine("<hr style='border:0; border-top:1px solid #000; width:100%;'/>");
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


}

