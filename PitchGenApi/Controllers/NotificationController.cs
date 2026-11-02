using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PitchGenApi.Database;

namespace PitchGenApi.Controllers
{
    [ApiController]
    [Authorize]
    [Route("api/[controller]")]
    public class NotificationController : ControllerBase
    {
        private readonly AppDbContext _context;
        public NotificationController(AppDbContext context) => _context = context;

        private async Task<int?> ResolveClientIdAsync(int? clientId)
        {
            if (!int.TryParse(User.FindFirst("UserId")?.Value, out var callerId) || callerId <= 0)
                return null;
            if (!clientId.HasValue || clientId == callerId) return callerId;
            if (clientId <= 0) return null;
            var username = User.Identity?.Name;
            if (string.IsNullOrWhiteSpace(username)) return null;
            var isAdmin = await _context.ClientDetails.AsNoTracking()
                .AnyAsync(c => c.Id == callerId && c.Username == username && c.IsAdmin)
                || await _context.tbl_clientdetails.AsNoTracking()
                    .AnyAsync(c => c.ClientID == callerId && c.UserName == username && c.IsAdmin == true);
            return isAdmin && await _context.ClientDetails.AnyAsync(c => c.Id == clientId)
                ? clientId : null;
        }

        [HttpGet]
        public async Task<IActionResult> List([FromQuery] int? clientId, [FromQuery] int? beforeId)
        {
            var account = await ResolveClientIdAsync(clientId);
            if (!account.HasValue) return Forbid();
            var query = _context.Notifications.AsNoTracking().Where(n => n.ClientId == account);
            var unreadCount = await query.CountAsync(n => n.ReadAt == null);
            if (beforeId.HasValue) query = query.Where(n => n.Id < beforeId);
            var rows = await query.OrderByDescending(n => n.Id).Take(21)
                .Select(n => new { n.Id, n.ContactId, n.Email, n.Source, n.CreatedAt,
                    isRead = n.ReadAt != null,
                    contactName = _context.contacts.Where(c => c.id == n.ContactId)
                        .Select(c => c.full_name).FirstOrDefault() })
                .ToListAsync();
            var items = rows.Take(20).ToList();
            return Ok(new { items, unreadCount, hasMore = rows.Count > 20,
                nextCursor = items.Count > 0 ? (int?)items[^1].Id : null });
        }

        public class MarkReadRequest { public int ThroughId { get; set; } }

        [HttpPost("read")]
        public async Task<IActionResult> MarkRead([FromQuery] int? clientId, [FromBody] MarkReadRequest request)
        {
            var account = await ResolveClientIdAsync(clientId);
            if (!account.HasValue) return Forbid();
            if (request.ThroughId <= 0) return BadRequest(new { message = "A notification ID is required." });
            await _context.Notifications
                .Where(n => n.ClientId == account && n.Id <= request.ThroughId && n.ReadAt == null)
                .ExecuteUpdateAsync(setters => setters.SetProperty(n => n.ReadAt, (DateTime?)DateTime.UtcNow));
            return NoContent();
        }
    }
}
