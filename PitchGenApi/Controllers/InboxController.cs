using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PitchGenApi.Database;
using PitchGenApi.Interfaces;
using PitchGenApi.Model;
using PitchGenApi.Model.DTOs;
using System.Net;

[ApiController]
[Route("api/[controller]")]
public class InboxController : ControllerBase
{
    private readonly IInboxRepository _repo;
    private readonly AppDbContext _context;
    private readonly IInboxEmailSyncService _inbox;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IInboxRefreshJob _inboxRefreshJob;
    public InboxController(IInboxRepository repo, AppDbContext context, IInboxEmailSyncService inbox, IServiceScopeFactory scopeFactory, IInboxRefreshJob inboxRefreshJob)
    {
        _repo = repo;
        _context = context;
        _inbox = inbox;
        _scopeFactory = scopeFactory;
        _inboxRefreshJob = inboxRefreshJob;
    }

    [HttpGet("Get-Inboxcredentials")]
    public async Task<IActionResult> Get([FromQuery]int clientId)
    {
        var setting = await _repo.GetByUserIdAsync(clientId);
        if (setting == null) return NotFound();
        return Ok(setting);
    }

    [HttpPost("Create-Inboxcredentials")]
    public async Task<IActionResult> Create([FromBody] InboxcredentialsDTO dto)
    {
        var existing = await _repo.GetByUserNameAsync(dto.ClientId, dto.Username);

        if (existing != null)
            return BadRequest("Email credentials already exist for this user.");
        var smtp = await _context.SmtpCredentials.FirstOrDefaultAsync(s => s.Username == dto.Username && s.ClientId == dto.ClientId.ToString());

        if (smtp == null)
            return BadRequest("Please add outbox first.");

        var isValid = await _repo.ValidateAsync(dto);

        if (!isValid)
            return BadRequest("Invalid email credentials or unable to connect to server.");

        var entity = new Inboxcredentials
        {
            ClientId = dto.ClientId,
            EmailAddress = dto.EmailAddress,
            Host = dto.Host,
            Port = dto.Port,
            Protocol = "IMAP",
            Username = dto.Username,
            Password = EncryptPassword(dto.Password),
            Outboxid = smtp.Id,
            encryption = dto.encryption,
            FullInboxSync = dto.FullInboxSync,
            //SyncIntervalMinutes = dto.SyncIntervalMinutes,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await _repo.AddAsync(entity);
        return Ok(entity);
    }

    [HttpPost("update-Inboxcredentials")]
    public async Task<IActionResult> Update([FromQuery] int id, [FromBody] InboxcredentialsDTO dto)
    {

        var existing = await _repo.GetByIdAsync(id);
        if (existing == null) return NotFound();

        var isValid = await _repo.ValidateAsync(dto);

        if (!isValid)
            return BadRequest("Invalid email credentials or unable to connect to server.");

        existing.EmailAddress = dto.EmailAddress;
        existing.Protocol = "IMAP";
        existing.Host = dto.Host;
        existing.Port = dto.Port;
        existing.Username = dto.Username;
        existing.FullInboxSync = dto.FullInboxSync;
        existing.Password = EncryptPassword(dto.Password);
        //existing.SyncIntervalMinutes = dto.SyncIntervalMinutes;
        existing.UpdatedAt = DateTime.UtcNow;

        await _repo.UpdateAsync(existing);
        return Ok(existing);
    }

    [HttpPost("delete-Inboxcredentials")]
    public async Task<IActionResult> Delete([FromQuery] int id)
    {
        await _repo.DeleteAsync(id);
        return NoContent();
    }

    [HttpGet("inbox")]
    public async Task<IActionResult> GetRepliesByInbox([FromQuery]int inboxId, [FromQuery] int clientId ,[FromQuery] string Provider, [FromQuery] int pageNumber = 1, [FromQuery] int pageSize = 10)
    {
    var data = await _repo.GetInboxThreads(inboxId,clientId, Provider, pageNumber, pageSize);

        return Ok(new
        {
            success = true,
            data
        });
    }

    [HttpGet("Inbox_dropdown")]
    public async Task<IActionResult> GetInboxPickList([FromQuery] int clientId)
    {
        var data = await _repo.GetInboxPickListByClientIdAsync(clientId);

        return Ok(new
        {
            success = true,
            count = data?.Count ?? 0,
            data = data
        });
    }

    [HttpPost("mark-read")]
    public async Task<IActionResult> MarkAsRead([FromQuery] string id)
    {
        var result = await _repo.MarkEmailAsUnassignedReadAsync(id);

        if (!result)
            return NotFound(new { success = false, message = "Email not found" });

        return Ok(new { success = true, message = "Marked as read" });
    }
    private string EncryptPassword(string plain)
    {
        // Implement AES or KeyVault encryption here
        return plain; // placeholder
    }


    [HttpPost("RefreshInbox")]
    public async Task<IActionResult> RefreshInbox([FromQuery] int clientId,[FromQuery] int inboxId,[FromQuery] string provider)
    {
        try
        {
            if (clientId <= 0)
            {
                return BadRequest(new
                {
                    message = "Invalid clientId"
                });
            }

            if (inboxId <= 0)
            {
                return BadRequest(new
                {
                    message = "Invalid inboxId"
                });
            }

            if (string.IsNullOrWhiteSpace(provider))
            {
                return BadRequest(new
                {
                    message = "Provider is required"
                });
            }

            provider = provider.ToUpper();

            bool selectedExists;

            if (provider == "GMAIL" || provider == "OUTLOOK")
            {
                selectedExists = await _context.EmailOAuthTokens
                    .AnyAsync(x =>
                        x.Id == inboxId &&
                        x.ClientId == clientId &&
                        x.Provider.ToUpper() == provider);
            }
            else if (provider == "IMAP" || provider == "SMTP")
            {
                selectedExists = await _context.Inboxcredentials
                    .AnyAsync(x =>
                        x.Id == inboxId &&
                        x.ClientId == clientId);
            }
            else
            {
                return BadRequest(new
                {
                    message = "Invalid provider. Use IMAP, OUTLOOK, or GMAIL."
                });
            }

            if (!selectedExists)
            {
                return NotFound(new
                {
                    message = "Selected inbox not found for this client"
                });
            }

            var selectedResult = await _inboxRefreshJob.RunSelectedAsync(inboxId, provider);

            _ = Task.Run(async () =>
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();

                    var job = scope.ServiceProvider.GetRequiredService<IInboxRefreshJob>();

                    await job.RunOtherClientInboxesAsync(clientId, inboxId, provider);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("❌ Background refresh failed: " + ex.Message);

                    if (ex.InnerException != null)
                    {
                        Console.WriteLine("❌ Inner error: " + ex.InnerException.Message);
                    }
                }
            });

            return Ok(new
            {
                message = selectedResult,
                backgroundMessage = "Other inboxes refresh started in background"
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new
            {
                message = "Unexpected error",
                error = ex.Message,
                innerError = ex.InnerException?.Message
            });
        }
    }

    [HttpPost("delete-conversation")]
    public async Task<IActionResult> DeleteConversation(DeleteConversationDto dto)

    {
        var result = await _repo.DeleteConversationAsync(dto);

        return Ok(new
        {
            success = true,
            message = result
        });
    }

    [HttpGet("get_unassigned_inbox")]
    public async Task<IActionResult> GetInboxEmails(int clientId, int inboxId, string Provider, int pageNumber = 1, int pageSize = 10)
     {
        var data = await _repo.GetInboxEmails(
            clientId,
            inboxId,
            Provider,
            pageNumber,
            pageSize);

        return Ok(new
        {
            success = true,
            data
        });
    }

    [HttpGet("get_sent_only")]
    public async Task<IActionResult> GetSentOnlyThreads([FromQuery] int inboxId, [FromQuery] string Provider, [FromQuery] int pageNumber = 1, [FromQuery] int pageSize = 10)
    {
        var data = await _repo.GetSentOnlyThreads(inboxId, Provider, pageNumber, pageSize);

        return Ok(new
        {
            success = true,
            data
        });
    }

    [HttpGet("get_combined_inbox_threads")]
    public async Task<IActionResult> GetCombinedInboxThreads([FromQuery] int clientId, [FromQuery] int inboxId, [FromQuery] string provider, [FromQuery] int pageNumber = 1, [FromQuery] int pageSize = 10)
    {
        var data = await _repo.GetCombinedInboxThreadsAsync(clientId, inboxId, provider, pageNumber, pageSize);
        return Ok(new
        {
            success = true,
            data
        });
    }

    [HttpGet("contact-threads")]
    public async Task<IActionResult> GetContactThreads([FromQuery] int clientId, [FromQuery] int contactId, [FromQuery] int pageNumber = 1, [FromQuery] int pageSize = 50)
    {
        if (clientId <= 0 || contactId <= 0)
        {
            return BadRequest(new { success = false, message = "clientId and contactId are required" });
        }

        var contact = await _context.contacts
            .AsNoTracking()
            .Where(x => x.id == contactId)
            .Select(x => new { x.id, x.email, x.full_name, x.first_name, x.last_name })
            .FirstOrDefaultAsync();
        
        if (contact == null)
        {
            return NotFound(new { success = false, message = "Contact not found" });
        }

        var contactEmail = (contact.email ?? string.Empty).Trim().ToLower();
        var hasContactEmail = !string.IsNullOrWhiteSpace(contactEmail);

        var seedSentEmails = await _context.EmailLogs
            .AsNoTracking()
            .Where(x =>
                x.ClientId == clientId &&
                x.TrackingId.HasValue &&
                x.IsSuccess &&
                (
                    x.ContactId == contactId ||
                    (hasContactEmail && x.ToEmail != null && x.ToEmail.ToLower() == contactEmail)
                ))
            .ToListAsync();

        var seedInboxEmails = await _context.InboxEmails
            .AsNoTracking()
            .Where(x =>
                x.ClientId == clientId &&
                x.TrackingId.HasValue &&
                (
                    x.Contactid == contactId ||
                    (hasContactEmail && x.FromEmail != null && x.FromEmail.ToLower() == contactEmail) ||
                    (hasContactEmail && x.ToEmail != null && x.ToEmail.ToLower() == contactEmail)
                ))
            .ToListAsync();

        var seedReplies = await _context.EmailReplies
            .AsNoTracking()
            .Where(x =>
                x.ClientId == clientId &&
                x.TrackingId.HasValue &&
                (
                    x.ContactId == contactId ||
                    (hasContactEmail && x.FromEmail != null && x.FromEmail.ToLower() == contactEmail) ||
                    (hasContactEmail && x.ToEmail != null && x.ToEmail.ToLower() == contactEmail)
                ))
            .ToListAsync();

        var trackingIds = seedSentEmails
            .Where(x => x.TrackingId.HasValue)
            .Select(x => x.TrackingId!.Value)
            .Union(seedInboxEmails.Where(x => x.TrackingId.HasValue).Select(x => x.TrackingId!.Value))
            .Union(seedReplies.Where(x => x.TrackingId.HasValue).Select(x => x.TrackingId!.Value))
            .Distinct()
            .ToList();

        if (!trackingIds.Any())
        {
            return Ok(new
            {
                success = true,
                data = new PagedInboxEmailDto
                {
                    TotalCount = 0,
                    TotalPages = 0,
                    PageNumber = pageNumber,
                    PageSize = pageSize,
                    Data = new List<EmailThreadDto>()
                }
            });
        }

        var sentEmails = await _context.EmailLogs
            .AsNoTracking()
            .Where(x =>
                x.ClientId == clientId &&
                x.TrackingId.HasValue &&
                trackingIds.Contains(x.TrackingId.Value) &&
                x.IsSuccess)
            .ToListAsync();

        var inboxEmails = await _context.InboxEmails
            .AsNoTracking()
            .Where(x =>
                x.ClientId == clientId &&
                x.TrackingId.HasValue &&
                trackingIds.Contains(x.TrackingId.Value))
            .ToListAsync();

        var replies = await _context.EmailReplies
            .AsNoTracking()
            .Where(x =>
                x.ClientId == clientId &&
                x.TrackingId.HasValue &&
                trackingIds.Contains(x.TrackingId.Value))
            .ToListAsync();

        var allMessageIds = sentEmails
            .Select(x => x.MessageId)
            .Union(inboxEmails.Select(x => x.MessageId))
            .Union(replies.Select(x => x.MessageId))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct()
            .ToList();

        var attachments = await _context.EmailAttachments
            .AsNoTracking()
            .Where(x => allMessageIds.Contains(x.MessageId))
            .ToListAsync();

        var pinnedTrackingIds = await _context.PinnedEmails
            .AsNoTracking()
            .Where(x => x.ClientId == clientId)
            .Select(x => x.TrackingId)
            .ToListAsync();

        var imapInboxMap = await _context.Inboxcredentials
            .Where(x => x.ClientId == clientId)
            .ToDictionaryAsync(x => x.Outboxid, x => x.Id);

        var oauthInboxMap = await _context.EmailOAuthTokens
            .Where(x => x.ClientId == clientId)
            .ToDictionaryAsync(x => x.Id, x => x.Id);

        var threads = trackingIds
            .Select(trackingId =>
            {
                var messages = new List<EmailConvDto>();

                messages.AddRange(inboxEmails
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
                        ContactId = i.Contactid ?? contactId,
                        ContactName = i.FromName ?? contact.full_name,
                        Inboxid = i.InboxId,
                        Provider = i.Provider,
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
                    }));

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
                        ContactId = s.ContactId ?? contactId,
                        ContactName = s.EmailRecipientName ?? contact.full_name,
                        Inboxid =s.outboxid.HasValue
                                ? (
                                    s.Provider == "SMTP"
                                        ? (imapInboxMap.TryGetValue(s.outboxid.Value, out var imapInboxId) ? imapInboxId : 0)
                                        : (oauthInboxMap.TryGetValue(s.outboxid.Value, out var oauthInboxId) ? oauthInboxId : 0)
                                  )
                                : 0,
                        Provider = s.Provider,
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
                    }));

                messages.AddRange(replies
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
                        ContactId = r.ContactId ?? contactId,
                        ContactName = r.FromName ?? contact.full_name ?? r.FromEmail,
                        Inboxid = r.Inboxid,
                        Provider = r.Provider,
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
                    }));

                messages = messages
                    .OrderBy(x => x.Date)
                    .GroupBy(x => string.IsNullOrWhiteSpace(x.MessageId) ? $"{x.Type}-{x.Date:o}-{x.FromEmail}-{x.ToEmail}" : x.MessageId)
                    .Select(g => g.First())
                    .ToList();

                if (!messages.Any())
                    return null;

                var latestMessage = messages.OrderByDescending(x => x.Date).First();
                var firstMessage = messages.First();

                return new EmailThreadDto
                {
                    TrackingId = trackingId,
                    Subject = latestMessage.Subject ?? firstMessage.Subject,
                    ContactEmail = contact.email,
                    ContactId = contactId,
                    TotalMessages = messages.Count,
                    LastMessageDate = messages.Max(x => x.Date),
                    HasUnread = messages.Any(x => (x.Type == "Inbox" || x.Type == "Reply") && !x.IsRead),
                    IsPinned = pinnedTrackingIds.Contains(trackingId),
                    Messages = messages
                };
            })
            .Where(x => x != null)
            .OrderByDescending(x => x!.LastMessageDate)
            .ToList();

        var totalCount = threads.Count;
        var safePageNumber = Math.Max(1, pageNumber);
        var safePageSize = Math.Max(1, pageSize);
        var pagedThreads = threads
            .Skip((safePageNumber - 1) * safePageSize)
            .Take(safePageSize)
            .ToList();

        return Ok(new
        {
            success = true,
            data = new PagedInboxEmailDto
            {
                TotalCount = totalCount,
                TotalPages = (int)Math.Ceiling(totalCount / (double)safePageSize),
                PageNumber = safePageNumber,
                PageSize = safePageSize,
                Data = pagedThreads
            }
        });
    }

    [HttpPost("mark-unassigned-read")]
    public async Task<IActionResult> MarkAsUnassignedRead([FromQuery] string id)
    {
        var result = await _repo.MarkEmailAsUnassignedReadAsync(id);

        if (!result)
            return NotFound(new { success = false, message = "Email not found" });

        return Ok(new { success = true, message = "Marked as read" });
    }
    // Controller
    [HttpGet("unread-count")]
    public async Task<IActionResult> GetUnreadCounts([FromQuery]int clientId)
    {
        var result = await _repo.GetTotalUnreadCountAsync(clientId);
        return Ok(result);
    }

    [HttpGet("download/{id:int}")]
    public async Task<IActionResult> DownloadAttachment(int id)
    {
        var attachment = await _context.EmailAttachments
            .FirstOrDefaultAsync(x => x.Id == id);

        if (attachment == null)
            return NotFound("Attachment not found");

        var filePath = Path.Combine(
            Directory.GetCurrentDirectory(),
            "wwwroot",
            attachment.FilePath.TrimStart('/')
                .Replace("/", Path.DirectorySeparatorChar.ToString()));

        if (!System.IO.File.Exists(filePath))
            return NotFound("File not found");

        var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);

        return File(
            stream,
            attachment.ContentType ?? "application/octet-stream",
            attachment.OriginalFileName);
    }

    [HttpPost("pin_email")]
    public async Task<IActionResult> TogglePin([FromQuery] int ClientId, [FromQuery] Guid TrackingId)
    {
        var result = await _repo.TogglePinAsync(ClientId,TrackingId);

        return Ok(new
        {
            Success = true,
            Message = result
        });
    }

    [HttpGet("pinned-emails")]
    public async Task<IActionResult> GetPinnedEmails(int clientId,int contactId)
    {
        var result = await _repo.GetPinnedEmails(clientId, contactId);

        return Ok(result);
    }
    [HttpGet("email-trail")]
    public async Task<IActionResult> GetEmailTrail(Guid trackingId)
    {
        var trail = await _repo.GetLatestEmailTrailAsync(trackingId);

        if (trail == null)
            return NotFound();

        return Ok(new
        {
            emailTrail = trail
        });
    }
}