using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PitchGenApi.Database;
using PitchGenApi.Interfaces;
using PitchGenApi.Model;
using PitchGenApi.Model.DTOs;

[ApiController]
[Route("api/[controller]")]
public class InboxController : ControllerBase
{
    private readonly IInboxRepository _repo;
    private readonly AppDbContext _context;
    private readonly ILogger<InboxController> _logger;

    public InboxController(IInboxRepository repo, AppDbContext context, ILogger<InboxController> logger)
    {
        _repo = repo;
        _context = context;
        _logger = logger;
    }

    [HttpGet("Get-Inboxcredentials")]
    public async Task<IActionResult> Get([FromQuery] int clientId)
    {
        try
        {
            var setting = await _repo.GetByUserIdAsync(clientId);
            if (setting == null) return NotFound();
            return Ok(setting);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Get-Inboxcredentials failed for clientId {ClientId}", clientId);
            return StatusCode(500, "An error occurred while retrieving inbox credentials.");
        }
    }

    [HttpPost("Create-Inboxcredentials")]
    public async Task<IActionResult> Create([FromBody] InboxcredentialsDTO dto)
    {
        try
        {
            var existing = await _repo.GetByUserNameAsync(dto.ClientId, dto.Username, dto.Protocol);

            if (existing != null)
                return BadRequest("Email credentials already exist for this user.");

            var smtp = await _context.SmtpCredentials.FirstOrDefaultAsync(s => s.Username == dto.Username && s.ClientId == dto.ClientId.ToString());

            if (smtp == null)
                return BadRequest("Please add inbox first.");

            var isValid = await _repo.ValidateAsync(dto);

            if (!isValid)
                return BadRequest("Invalid email credentials or unable to connect to server.");

            var entity = new Inboxcredentials
            {
                ClientId = dto.ClientId,
                EmailAddress = dto.EmailAddress,
                Protocol = dto.Protocol,
                Host = dto.Host,
                Port = dto.Port,
                UseSSL = dto.UseSSL,
                Username = dto.Username,
                Password = EncryptPassword(dto.Password),
                Outboxid = smtp.Id,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            await _repo.AddAsync(entity);
            return Ok(entity);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Create-Inboxcredentials failed for clientId {ClientId}, username {Username}", dto.ClientId, dto.Username);
            return StatusCode(500, "An error occurred while creating inbox credentials.");
        }
    }

    [HttpPost("update-Inboxcredentials")]
    public async Task<IActionResult> Update([FromQuery] int id, [FromBody] InboxcredentialsDTO dto)
    {
        try
        {
            var existing = await _repo.GetByIdAsync(id);
            if (existing == null) return NotFound();

            var isValid = await _repo.ValidateAsync(dto);

            if (!isValid)
                return BadRequest("Invalid email credentials or unable to connect to server.");

            existing.EmailAddress = dto.EmailAddress;
            existing.Protocol = dto.Protocol;
            existing.Host = dto.Host;
            existing.Port = dto.Port;
            existing.UseSSL = dto.UseSSL;
            existing.Username = dto.Username;
            existing.Password = EncryptPassword(dto.Password);
            existing.UpdatedAt = DateTime.UtcNow;

            await _repo.UpdateAsync(existing);
            return Ok(existing);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "update-Inboxcredentials failed for id {Id}", id);
            return StatusCode(500, "An error occurred while updating inbox credentials.");
        }
    }

    [HttpPost("delete-Inboxcredentials")]
    public async Task<IActionResult> Delete([FromQuery] int id)
    {
        try
        {
            await _repo.DeleteAsync(id);
            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "delete-Inboxcredentials failed for id {Id}", id);
            return StatusCode(500, "An error occurred while deleting inbox credentials.");
        }
    }

    [HttpGet("inbox")]
    public async Task<IActionResult> GetRepliesByInbox([FromQuery] int inboxId)
    {
        try
        {
            var data = await _repo.GetRepliesByInboxIdAsync(inboxId);
            return Ok(new { success = true, count = data.Count, data });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetRepliesByInbox failed for inboxId {InboxId}", inboxId);
            return StatusCode(500, "An error occurred while retrieving inbox replies.");
        }
    }

    [HttpGet("Inbox_dropdown")]
    public async Task<IActionResult> GetInboxPickList([FromQuery] int clientId)
    {
        try
        {
            var data = await _repo.GetInboxPickListByClientIdAsync(clientId);
            return Ok(new { success = true, count = data.Count, data });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetInboxPickList failed for clientId {ClientId}", clientId);
            return StatusCode(500, "An error occurred while retrieving inbox dropdown.");
        }
    }

    private string EncryptPassword(string plain)
    {
        // Implement AES or KeyVault encryption here
        return plain; // placeholder
    }
}
