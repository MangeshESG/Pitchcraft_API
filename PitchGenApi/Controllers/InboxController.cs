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

    public InboxController(IInboxRepository repo, AppDbContext context, IInboxEmailSyncService inbox)
    {
        _repo = repo;
        _context = context;
        _inbox = inbox;
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
    public async Task<IActionResult> GetRepliesByInbox([FromQuery]int inboxId, [FromQuery] string Provider, [FromQuery] int pageNumber = 1, [FromQuery] int pageSize = 10)
    {
        var data = await _repo.GetInboxThreads(inboxId, Provider, pageNumber, pageSize);

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
    public async Task<IActionResult> RefreshInbox([FromQuery] int inboxId, [FromQuery] string provider)
    {
        try
        {
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

            EmailOAuthTokens? oauth = null;

            // OAuth provider check
            if (provider == "OUTLOOK" || provider == "GMAIL")
            {
                oauth = await _context.EmailOAuthTokens
                    .FirstOrDefaultAsync(x =>
                        x.Id == inboxId &&
                        x.Provider.ToUpper() == provider);

                if (oauth == null)
                {
                    return BadRequest(new
                    {
                        message = $"OAuth token not found for {provider}"
                    });
                }
            }

            switch (provider)
            {
                case "IMAP":
                case "SMTP":
                    {
                        var imap = await _context.Inboxcredentials
                            .FirstOrDefaultAsync(x => x.Id == inboxId);

                        if (imap == null)
                        {
                            return NotFound(new
                            {
                                message = "Inbox credential not found"
                            });
                        }

                        await _inbox.SyncEmailsAsync(imap);
                        break;
                    }

                case "OUTLOOK":
                    {
                        await _inbox.SyncOutlookInboxAsync(oauth);
                        break;
                    }

                case "GMAIL":
                    {
                        await _inbox.SyncGmailInboxAsync(oauth);
                        break;
                    }

                default:
                    return BadRequest(new
                    {
                        message = "Invalid provider. Use IMAP, OUTLOOK, or GMAIL."
                    });
            }

            return Ok(new
            {
                message = "Inbox refreshed successfully"
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new
            {
                message = "Unexpected error",
                error = ex.Message
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
}