using System.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PitchGenApi.Database;
using PitchGenApi.Model;

namespace PitchGenApi.Controllers
{
    [ApiController]
    [Authorize]
    [Route("api/crm/custom-attribute-groups")]
    public class CustomAttributeGroupsController : ControllerBase
    {
        private readonly AppDbContext _context;

        public CustomAttributeGroupsController(AppDbContext context) => _context = context;

        private int? CurrentClientId =>
            int.TryParse(User.FindFirst("UserId")?.Value, out var clientId) && clientId > 0
                ? clientId : null;

        [HttpGet]
        public async Task<IActionResult> List(int page = 1, int pageSize = 20, string? search = null)
        {
            if (CurrentClientId is not int clientId) return Unauthorized();
            page = Math.Max(page, 1);
            pageSize = Math.Clamp(pageSize, 1, 100);
            var query = _context.crm_custom_field_groups.AsNoTracking().Where(g => g.client_id == clientId);
            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim();
                query = query.Where(g => g.name.StartsWith(term));
            }

            var total = await query.CountAsync();
            var items = await query.OrderBy(g => g.sort_order).ThenBy(g => g.id)
                .Skip((page - 1) * pageSize).Take(pageSize)
                .Select(g => new
                {
                    g.id,
                    g.name,
                    g.description,
                    g.created_at,
                    g.sort_order,
                    attributeCount = _context.crm_custom_fields.Count(f => f.client_id == clientId && f.group_id == g.id)
                }).ToListAsync();
            return Ok(new { items, total, page, pageSize });
        }

        [HttpGet("attributes")]
        public async Task<IActionResult> ListAttributes(int page = 1, int pageSize = 20,
            string? search = null, int? groupId = null)
        {
            if (CurrentClientId is not int clientId) return Unauthorized();
            page = Math.Max(page, 1);
            pageSize = Math.Clamp(pageSize, 1, 100);
            if (groupId.HasValue && !await _context.crm_custom_field_groups
                    .AnyAsync(g => g.id == groupId && g.client_id == clientId))
                return NotFound("Group not found.");

            var query = _context.crm_custom_fields.AsNoTracking().Where(f => f.client_id == clientId);
            if (groupId.HasValue) query = query.Where(f => f.group_id == groupId);
            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim();
                query = query.Where(f => f.field_name.StartsWith(term));
            }

            var total = await query.CountAsync();
            var items = await query.OrderBy(f => f.sort_order).ThenBy(f => f.id)
                .Skip((page - 1) * pageSize).Take(pageSize)
                .Select(f => new
                {
                    f.id,
                    f.field_name,
                    f.field_key,
                    f.field_type,
                    f.options_json,
                    f.group_id,
                    f.sort_order,
                    groupName = _context.crm_custom_field_groups
                        .Where(g => g.id == f.group_id && g.client_id == clientId)
                        .Select(g => g.name).FirstOrDefault()
                }).ToListAsync();
            return Ok(new { items, total, page, pageSize });
        }

        [HttpGet("layout")]
        public async Task<IActionResult> Layout()
        {
            if (CurrentClientId is not int clientId) return Unauthorized();
            var groups = await _context.crm_custom_field_groups.AsNoTracking()
                .Where(g => g.client_id == clientId)
                .OrderBy(g => g.sort_order).ThenBy(g => g.id)
                .Select(g => new { g.id, g.name, g.sort_order }).ToListAsync();
            var attributes = await _context.crm_custom_fields.AsNoTracking()
                .Where(f => f.client_id == clientId)
                .OrderBy(f => f.sort_order).ThenBy(f => f.id)
                .Select(f => new
                {
                    f.id, f.field_name, f.field_key, f.field_type,
                    f.options_json, f.group_id, f.sort_order
                }).ToListAsync();
            return Ok(new { groups, attributes });
        }

        [HttpPost("order")]
        public async Task<IActionResult> ReorderGroups([FromBody] ReorderGroupPageRequest request)
        {
            if (CurrentClientId is not int clientId) return Unauthorized();
            if (request == null || request.Page < 1 || request.PageSize < 1 || request.PageSize > 100 ||
                request.OrderedIds == null || request.OrderedIds.Count == 0 ||
                request.OrderedIds.Count > request.PageSize ||
                request.OrderedIds.Distinct().Count() != request.OrderedIds.Count)
                return BadRequest("Invalid group order.");

            var strategy = _context.Database.CreateExecutionStrategy();
            return await strategy.ExecuteAsync<IActionResult>(async () =>
            {
                await using var transaction = await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable);
                var rows = await _context.crm_custom_field_groups
                    .Where(g => g.client_id == clientId)
                    .OrderBy(g => g.sort_order).ThenBy(g => g.id)
                    .Skip((request.Page - 1) * request.PageSize).Take(request.PageSize).ToListAsync();
                if (rows.Count != request.OrderedIds.Count ||
                    !rows.Select(g => g.id).ToHashSet().SetEquals(request.OrderedIds))
                    return Conflict("Groups changed. Refresh and try again.");
                var positions = rows.Select(g => g.sort_order).ToArray();
                var byId = rows.ToDictionary(g => g.id);
                for (var i = 0; i < positions.Length; i++) byId[request.OrderedIds[i]].sort_order = positions[i];
                await _context.SaveChangesAsync();
                await transaction.CommitAsync();
                return NoContent();
            });
        }

        [HttpPost("{groupId:int}/attributes/order")]
        public async Task<IActionResult> ReorderAttributes(int groupId, [FromBody] ReorderGroupPageRequest request)
        {
            if (CurrentClientId is not int clientId) return Unauthorized();
            if (request == null || request.Page < 1 || request.PageSize < 1 || request.PageSize > 100 ||
                request.OrderedIds == null || request.OrderedIds.Count == 0 ||
                request.OrderedIds.Count > request.PageSize ||
                request.OrderedIds.Distinct().Count() != request.OrderedIds.Count)
                return BadRequest("Invalid attribute order.");
            if (!await _context.crm_custom_field_groups.AnyAsync(g => g.id == groupId && g.client_id == clientId))
                return NotFound("Group not found.");

            var strategy = _context.Database.CreateExecutionStrategy();
            return await strategy.ExecuteAsync<IActionResult>(async () =>
            {
                await using var transaction = await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable);
                var rows = await _context.crm_custom_fields
                    .Where(f => f.client_id == clientId && f.group_id == groupId)
                    .OrderBy(f => f.sort_order).ThenBy(f => f.id)
                    .Skip((request.Page - 1) * request.PageSize).Take(request.PageSize).ToListAsync();
                if (rows.Count != request.OrderedIds.Count ||
                    !rows.Select(f => f.id).ToHashSet().SetEquals(request.OrderedIds))
                    return Conflict("Attributes changed. Refresh and try again.");
                var positions = rows.Select(f => f.sort_order).ToArray();
                var byId = rows.ToDictionary(f => f.id);
                for (var i = 0; i < positions.Length; i++) byId[request.OrderedIds[i]].sort_order = positions[i];
                await _context.SaveChangesAsync();
                await transaction.CommitAsync();
                return NoContent();
            });
        }

        [HttpPost]
        public Task<IActionResult> Create([FromBody] SaveCustomAttributeGroupRequest request) =>
            Save(null, request);

        [HttpPost("{id:int}")]
        public Task<IActionResult> Update(int id, [FromBody] SaveCustomAttributeGroupRequest request) =>
            Save(id, request);

        private async Task<IActionResult> Save(int? id, SaveCustomAttributeGroupRequest request)
        {
            if (CurrentClientId is not int clientId) return Unauthorized();
            if (request == null) return BadRequest("Group details are required.");
            var name = request.Name?.Trim();
            if (string.IsNullOrWhiteSpace(name) || name.Length > 120)
                return BadRequest("Group name must be between 1 and 120 characters.");
            var description = request.Description?.Trim();
            if (description?.Length > 500) return BadRequest("Description is too long.");

            var addIds = (request.AddAttributeIds ?? new List<int>()).Distinct().ToArray();
            var removeIds = (request.RemoveAttributeIds ?? new List<int>()).Distinct().ToArray();
            if (addIds.Any(x => x <= 0) || removeIds.Any(x => x <= 0) ||
                addIds.Intersect(removeIds).Any() || addIds.Length + removeIds.Length > 10000)
                return BadRequest("Invalid attribute selection.");

            var strategy = _context.Database.CreateExecutionStrategy();
            return await strategy.ExecuteAsync<IActionResult>(async () =>
            {
                await using var transaction = await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable);
                try
                {
                CrmCustomFieldGroup group;
                if (id.HasValue)
                {
                    group = await _context.crm_custom_field_groups.FirstOrDefaultAsync(
                        g => g.id == id && g.client_id == clientId);
                    if (group == null) return NotFound("Group not found.");
                }
                else
                {
                    var lastOrder = await _context.crm_custom_field_groups
                        .Where(g => g.client_id == clientId)
                        .MaxAsync(g => (long?)g.sort_order) ?? 0;
                    group = new CrmCustomFieldGroup
                    {
                        client_id = clientId,
                        created_at = DateTime.UtcNow,
                        sort_order = lastOrder + 1024
                    };
                    _context.crm_custom_field_groups.Add(group);
                }

                if (await _context.crm_custom_field_groups.AnyAsync(g =>
                    g.client_id == clientId && g.id != group.id && g.name == name))
                    return Conflict("A group with this name already exists.");

                group.name = name;
                group.description = description;
                group.updated_at = DateTime.UtcNow;
                await _context.SaveChangesAsync();

                if (removeIds.Length > 0)
                {
                    var removed = await _context.crm_custom_fields
                        .Where(f => f.client_id == clientId && f.group_id == group.id &&
                                    removeIds.Contains(f.id))
                        .ExecuteUpdateAsync(s => s.SetProperty(f => f.group_id, (int?)null));
                    if (removed != removeIds.Length)
                        return Conflict("One or more attributes no longer belong to this group. Refresh and try again.");
                }

                if (addIds.Length > 0)
                {
                    var lastAttributeOrder = await _context.crm_custom_fields
                        .Where(f => f.client_id == clientId && f.group_id == group.id)
                        .MaxAsync(f => (long?)f.sort_order) ?? 0;
                    var addedFields = await _context.crm_custom_fields
                        .Where(f => f.client_id == clientId && addIds.Contains(f.id) &&
                                    (f.group_id == null || f.group_id == group.id))
                        .OrderBy(f => f.id).ToListAsync();
                    if (addedFields.Count != addIds.Length)
                        return Conflict("An attribute is already in another group. Refresh and try again.");
                    foreach (var field in addedFields.Where(f => f.group_id == null))
                    {
                        field.group_id = group.id;
                        field.sort_order = (lastAttributeOrder += 1024);
                    }
                    await _context.SaveChangesAsync();
                }

                    await transaction.CommitAsync();
                    return Ok(new { group.id, group.name });
                }
                catch (DbUpdateException)
                {
                    return Conflict("A group with this name already exists.");
                }
            });
        }

        [HttpDelete("{id:int}")]
        public async Task<IActionResult> Delete(int id)
        {
            if (CurrentClientId is not int clientId) return Unauthorized();
            var strategy = _context.Database.CreateExecutionStrategy();
            return await strategy.ExecuteAsync<IActionResult>(async () =>
            {
            await using var transaction = await _context.Database.BeginTransactionAsync();
            var group = await _context.crm_custom_field_groups
                .FirstOrDefaultAsync(g => g.id == id && g.client_id == clientId);
            if (group == null) return NotFound("Group not found.");

            await _context.crm_custom_fields
                .Where(f => f.client_id == clientId && f.group_id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(f => f.group_id, (int?)null));
            _context.crm_custom_field_groups.Remove(group);
            await _context.SaveChangesAsync();
            await transaction.CommitAsync();
            return NoContent();
            });
        }
    }

    public class SaveCustomAttributeGroupRequest
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public List<int> AddAttributeIds { get; set; } = new();
        public List<int> RemoveAttributeIds { get; set; } = new();
    }

    public class ReorderGroupPageRequest
    {
        public int Page { get; set; }
        public int PageSize { get; set; }
        public List<int> OrderedIds { get; set; } = new();
    }
}
