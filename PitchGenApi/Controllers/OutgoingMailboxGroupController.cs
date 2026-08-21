using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PitchGenApi.Database;
using PitchGenApi.Model;

namespace PitchGenApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class OutgoingMailboxGroupController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly ILogger<OutgoingMailboxGroupController> _logger;

        public OutgoingMailboxGroupController(
            AppDbContext context,
            ILogger<OutgoingMailboxGroupController> logger)
        {
            _context = context;
            _logger = logger;
        }

        [HttpGet("get")]
        public async Task<IActionResult> Get(int clientId)
        {
            try
            {
                if (clientId <= 0)
                    return BadRequest("Valid ClientId is required.");

                var groups = await _context.OutgoingMailboxGroups
                    .Where(x => x.ClientId == clientId)
                    .OrderBy(x => x.Name)
                    .ToListAsync();

                var groupIds = groups.Select(x => x.Id).ToList();
                var members = await _context.OutgoingMailboxGroupMembers
                    .Where(x => groupIds.Contains(x.GroupId))
                    .ToListAsync();

                return Ok(groups.Select(group => new
                {
                    group.Id,
                    group.ClientId,
                    group.Name,
                    group.Description,
                    group.CreatedAt,
                    group.UpdatedAt,
                    Members = members.Where(member => member.GroupId == group.Id).Select(member => new
                    {
                        member.Id,
                        member.OutboxId,
                        member.Provider
                    })
                }));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get outgoing mailbox groups for ClientId {ClientId}", clientId);
                return StatusCode(500, new
                {
                    message = "Failed to load outgoing groups.",
                    error = ex.InnerException?.Message ?? ex.Message
                });
            }
        }

        [HttpPost("update")]
        public async Task<IActionResult> Update([FromBody] SaveOutgoingMailboxGroupRequest request)
        {
            try
            {
                if (request == null || request.Id <= 0 || request.ClientId <= 0 || string.IsNullOrWhiteSpace(request.Name))
                    return BadRequest("Id, ClientId and group name are required.");

                var members = CleanMembers(request.Members);
                if (members.Count == 0)
                    return BadRequest("Select at least one mailbox.");

                var executionStrategy = _context.Database.CreateExecutionStrategy();
                var updated = await executionStrategy.ExecuteAsync(async () =>
                {
                    _context.ChangeTracker.Clear();
                    await using var transaction = await _context.Database.BeginTransactionAsync();
                    var group = await _context.OutgoingMailboxGroups.FirstOrDefaultAsync(x =>
                        x.Id == request.Id && x.ClientId == request.ClientId);
                    if (group == null) return false;

                    var duplicateName = await _context.OutgoingMailboxGroups.AnyAsync(x =>
                        x.ClientId == request.ClientId && x.Id != request.Id && x.Name == request.Name.Trim());
                    if (duplicateName)
                        throw new InvalidOperationException("A group with this name already exists.");

                    group.Name = request.Name.Trim();
                    group.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
                    group.UpdatedAt = DateTime.UtcNow;

                    var oldMembers = await _context.OutgoingMailboxGroupMembers
                        .Where(x => x.GroupId == group.Id)
                        .ToListAsync();
                    _context.OutgoingMailboxGroupMembers.RemoveRange(oldMembers);
                    _context.OutgoingMailboxGroupMembers.AddRange(members.Select(member => new OutgoingMailboxGroupMember
                    {
                        GroupId = group.Id,
                        OutboxId = member.OutboxId,
                        Provider = member.Provider,
                        CreatedAt = DateTime.UtcNow
                    }));
                    await _context.SaveChangesAsync();
                    await transaction.CommitAsync();
                    return true;
                });

                return updated
                    ? Ok(new { Message = "Outgoing group updated successfully." })
                    : NotFound("Outgoing group not found.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update outgoing group {GroupId}", request?.Id);
                return StatusCode(500, new { message = "Failed to update outgoing group.", error = ex.InnerException?.Message ?? ex.Message });
            }
        }

        [HttpPost("delete")]
        public async Task<IActionResult> Delete([FromBody] OutgoingMailboxGroupDeleteRequest request)
        {
            try
            {
                if (request == null || request.Id <= 0 || request.ClientId <= 0)
                    return BadRequest("Id and ClientId are required.");

                var executionStrategy = _context.Database.CreateExecutionStrategy();
                var deleted = await executionStrategy.ExecuteAsync(async () =>
                {
                    _context.ChangeTracker.Clear();
                    await using var transaction = await _context.Database.BeginTransactionAsync();
                    var group = await _context.OutgoingMailboxGroups.FirstOrDefaultAsync(x =>
                        x.Id == request.Id && x.ClientId == request.ClientId);
                    if (group == null) return false;

                    var members = await _context.OutgoingMailboxGroupMembers
                        .Where(x => x.GroupId == group.Id)
                        .ToListAsync();
                    _context.OutgoingMailboxGroupMembers.RemoveRange(members);
                    _context.OutgoingMailboxGroups.Remove(group);
                    await _context.SaveChangesAsync();
                    await transaction.CommitAsync();
                    return true;
                });

                return deleted
                    ? Ok(new { Message = "Outgoing group deleted successfully." })
                    : NotFound("Outgoing group not found.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete outgoing group {GroupId}", request?.Id);
                return StatusCode(500, new { message = "Failed to delete outgoing group.", error = ex.InnerException?.Message ?? ex.Message });
            }
        }

        [HttpPost("create")]
        public async Task<IActionResult> Create([FromBody] SaveOutgoingMailboxGroupRequest request)
        {
            try
            {
                if (request == null)
                    return BadRequest("Request data is required.");

                if (request.ClientId <= 0 || string.IsNullOrWhiteSpace(request.Name))
                    return BadRequest("ClientId and group name are required.");

                var members = CleanMembers(request.Members);
                if (members.Count == 0)
                    return BadRequest("Select at least one mailbox.");

                var nameExists = await _context.OutgoingMailboxGroups.AnyAsync(x =>
                    x.ClientId == request.ClientId && x.Name == request.Name.Trim());
                if (nameExists)
                    return BadRequest("A group with this name already exists.");

                var executionStrategy = _context.Database.CreateExecutionStrategy();
                var groupId = await executionStrategy.ExecuteAsync(async () =>
                {
                    _context.ChangeTracker.Clear();
                    await using var transaction = await _context.Database.BeginTransactionAsync();

                    var group = new OutgoingMailboxGroup
                    {
                        ClientId = request.ClientId,
                        Name = request.Name.Trim(),
                        Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
                        CreatedAt = DateTime.UtcNow
                    };

                    _context.OutgoingMailboxGroups.Add(group);
                    await _context.SaveChangesAsync();

                    _context.OutgoingMailboxGroupMembers.AddRange(members.Select(member => new OutgoingMailboxGroupMember
                    {
                        GroupId = group.Id,
                        OutboxId = member.OutboxId,
                        Provider = member.Provider,
                        CreatedAt = DateTime.UtcNow
                    }));
                    await _context.SaveChangesAsync();
                    await transaction.CommitAsync();

                    return group.Id;
                });

                return Ok(new { Id = groupId, Message = "Outgoing group created successfully." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create outgoing mailbox group for ClientId {ClientId}", request?.ClientId);
                return StatusCode(500, new
                {
                    message = "Failed to create outgoing group.",
                    error = ex.InnerException?.Message ?? ex.Message
                });
            }
        }

        private static List<OutgoingMailboxMemberRequest> CleanMembers(IEnumerable<OutgoingMailboxMemberRequest>? members)
        {
            return (members ?? Enumerable.Empty<OutgoingMailboxMemberRequest>())
                .Where(x => x.OutboxId > 0 && !string.IsNullOrWhiteSpace(x.Provider))
                .Select(x => new OutgoingMailboxMemberRequest
                {
                    OutboxId = x.OutboxId,
                    Provider = x.Provider.Trim()
                })
                .GroupBy(x => new { x.OutboxId, Provider = x.Provider.ToUpperInvariant() })
                .Select(x => x.First())
                .ToList();
        }
    }

    public class SaveOutgoingMailboxGroupRequest
    {
        public int Id { get; set; }
        public int ClientId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public List<OutgoingMailboxMemberRequest> Members { get; set; } = new();
    }

    public class OutgoingMailboxGroupDeleteRequest
    {
        public int Id { get; set; }
        public int ClientId { get; set; }
    }

    public class OutgoingMailboxMemberRequest
    {
        public int OutboxId { get; set; }
        public string Provider { get; set; } = string.Empty;
    }
}
