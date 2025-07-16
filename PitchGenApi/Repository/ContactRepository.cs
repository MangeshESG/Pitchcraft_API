using Microsoft.EntityFrameworkCore;
using PitchGenApi.Database;
using PitchGenApi.Model.DTOs;
using PitchGenApi.Models;

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
}

