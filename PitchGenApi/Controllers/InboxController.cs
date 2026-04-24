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

    public InboxController(IInboxRepository repo, AppDbContext context)
    {
        _repo = repo;
        _context = context;
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
        var existing = await _repo.GetByUserNameAsync(dto.ClientId,dto.Username,dto.Protocol);

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
        existing.Protocol = dto.Protocol;
        existing.Host = dto.Host;
        existing.Port = dto.Port;
        existing.UseSSL = dto.UseSSL;
        existing.Username = dto.Username;
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
    public async Task<IActionResult> GetRepliesByInbox([FromQuery]int inboxId, [FromQuery] string Provider)
    {
        var data = await _repo.GetInboxThreads(inboxId, Provider);

        return Ok(new
        {
            success = true,
            count = data.Count,
            data = data
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
    public async Task<IActionResult> MarkAsRead([FromQuery]int id)
    {
        var result = await _repo.MarkEmailAsReadAsync(id);

        if (!result)
            return NotFound(new { success = false, message = "Email not found" });

        return Ok(new { success = true, message = "Marked as read" });
    }
    private string EncryptPassword(string plain)
    {
        // Implement AES or KeyVault encryption here
        return plain; // placeholder
    }
}