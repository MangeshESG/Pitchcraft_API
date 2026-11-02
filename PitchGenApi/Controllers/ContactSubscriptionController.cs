using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PitchGenApi.Database;
using PitchGenApi.Models;

namespace PitchGenApi.Controllers
{
    [ApiController]
    [Authorize]
    [Route("api/Crm/contact-subscription")]
    public class ContactSubscriptionController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly ContactRepository _contacts;
        public ContactSubscriptionController(AppDbContext context, ContactRepository contacts)
        { _context = context; _contacts = contacts; }

        private async Task<int?> ResolveClientAsync(int clientId)
        {
            if (clientId <= 0 || !int.TryParse(User.FindFirst("UserId")?.Value, out var callerId) || callerId <= 0)
                return null;
            if (callerId == clientId) return clientId;
            var name = User.Identity?.Name;
            if (string.IsNullOrWhiteSpace(name)) return null;
            var admin = await _context.ClientDetails.AsNoTracking()
                .AnyAsync(c => c.Id == callerId && c.Username == name && c.IsAdmin)
                || await _context.tbl_clientdetails.AsNoTracking()
                    .AnyAsync(c => c.ClientID == callerId && c.UserName == name && c.IsAdmin == true);
            return admin && await _context.ClientDetails.AnyAsync(c => c.Id == clientId) ? clientId : null;
        }

        private Task<Contact?> FindContactAsync(int clientId, int contactId) =>
            _context.contacts.AsNoTracking().FirstOrDefaultAsync(c => c.id == contactId &&
                (_context.data_files.Any(f => f.id == c.DataFileId && f.client_id == clientId) ||
                 _context.segmentContacts.Any(s => s.ContactId == c.id &&
                    _context.segments.Any(g => g.Id == s.SegmentId && g.ClientId == clientId)) ||
                 _context.UnsubscribedContacts.Any(u => u.ClientId == clientId && u.ContactId == c.id)));

        [HttpGet]
        public async Task<IActionResult> Status([FromQuery] int clientId, [FromQuery] int contactId)
        {
            if (await ResolveClientAsync(clientId) == null) return Forbid();
            var contact = await FindContactAsync(clientId, contactId);
            if (contact == null) return NotFound(new { message = "Contact not found for this account." });
            var isUnsubscribed = await _context.UnsubscribedContacts.AnyAsync(u => u.ClientId == clientId &&
                (u.ContactId == contactId || (contact.email != null && u.Email == contact.email)));
            return Ok(new { contactId, isUnsubscribed });
        }

        public class SubscriptionRequest
        {
            public int ContactId { get; set; }
            public bool IsSubscribed { get; set; }
        }

        [HttpPost]
        public async Task<IActionResult> Update([FromQuery] int clientId, [FromBody] SubscriptionRequest request)
        {
            if (await ResolveClientAsync(clientId) == null) return Forbid();
            var contact = await FindContactAsync(clientId, request.ContactId);
            if (contact == null) return NotFound(new { message = "Contact not found for this account." });
            if (string.IsNullOrWhiteSpace(contact.email))
                return BadRequest(new { message = "This contact has no email address." });
            if (request.IsSubscribed)
            {
                // Remove every matching account-specific suppression, including
                // email-only records created from the contact list.
                await _context.UnsubscribedContacts.Where(u => u.ClientId == clientId &&
                    (u.ContactId == request.ContactId || u.Email == contact.email)).ExecuteDeleteAsync();
            }
            else
            {
                // Manual changes intentionally omit notificationSource.
                var success = await _contacts.UnsubscribeAsync(clientId: clientId,
                    contactId: request.ContactId, email: contact.email);
                if (!success) return BadRequest(new { message = "Unable to unsubscribe contact." });
            }
            return Ok(new { contactId = request.ContactId, isUnsubscribed = !request.IsSubscribed });
        }
    }
}
