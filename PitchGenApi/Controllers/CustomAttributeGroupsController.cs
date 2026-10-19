using System.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using PitchGenApi.Database;
using PitchGenApi.Model;

namespace PitchGenApi.Controllers
{
    [ApiController]
    [Route("api/crm/custom-attribute-groups")]
    public class CustomAttributeGroupsController : ControllerBase
    {
        private readonly AppDbContext _context;

        public CustomAttributeGroupsController(AppDbContext context) => _context = context;

        private int? CurrentClientId =>
            int.TryParse(User.FindFirst("UserId")?.Value, out var clientId) && clientId > 0
                ? clientId : null;

        private async Task<int?> ResolveClientIdAsync()
        {
            if (CurrentClientId is not int callerId) return null;
            if (!Request.Query.TryGetValue("clientId", out var selectedValue)) return callerId;
            if (!int.TryParse(selectedValue.ToString(), out var selectedId) || selectedId <= 0) return null;
            if (selectedId == callerId) return callerId;

            // The main app login issues UserId from ClientDetails; the legacy login
            // issues it from tbl_clientdetails. Resolve admin status in the same table
            // that issued this token so equal numeric IDs cannot cross-authorize.
            var isMainAppToken = User.FindFirst("firstname") != null;
            var isAdmin = isMainAppToken
                ? await _context.ClientDetails.AsNoTracking()
                    .AnyAsync(client => client.Id == callerId && client.IsAdmin)
                : await _context.tbl_clientdetails.AsNoTracking()
                    .AnyAsync(client => client.ClientID == callerId && client.IsAdmin == true);
            if (!isAdmin) return null;
            return await _context.ClientDetails.AsNoTracking().AnyAsync(client => client.Id == selectedId)
                ? selectedId : null;
        }

        [HttpGet]
        public async Task<IActionResult> List(int page = 1, int pageSize = 20, string? search = null)
        {
            if (await ResolveClientIdAsync() is not int clientId) return Unauthorized();
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
            string? search = null, int? groupId = null, bool ungroupedOnly = false, string? systemGroupKey = null)
        {
            if (await ResolveClientIdAsync() is not int clientId) return Unauthorized();
            page = Math.Max(page, 1);
            pageSize = Math.Clamp(pageSize, 1, 100);
            if (groupId.HasValue && !await _context.crm_custom_field_groups
                    .AnyAsync(g => g.id == groupId && g.client_id == clientId))
                return NotFound("Group not found.");
            systemGroupKey = systemGroupKey?.Trim().ToLowerInvariant();
            if (systemGroupKey != null && systemGroupKey != "personal" && systemGroupKey != "company")
                return BadRequest(new { message = "Invalid system group." });
            if (systemGroupKey != null && (groupId.HasValue || ungroupedOnly))
                return BadRequest(new { message = "Choose one group filter." });

            var query = _context.crm_custom_fields.AsNoTracking().Where(f => f.client_id == clientId);
            if (groupId.HasValue) query = query.Where(f => f.group_id == groupId);
            if (systemGroupKey != null) query = query.Where(f => f.assigned_system_group_key == systemGroupKey);
            if (ungroupedOnly) query = query.Where(f => f.group_id == null && f.assigned_system_group_key == null);
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
                    f.assigned_system_group_key,
                    f.sort_order,
                    groupName = f.assigned_system_group_key == "personal" ? "Personal" :
                        f.assigned_system_group_key == "company" ? "Company" :
                        _context.crm_custom_field_groups
                            .Where(g => g.id == f.group_id && g.client_id == clientId)
                            .Select(g => g.name).FirstOrDefault()
                }).ToListAsync();
            return Ok(new { items, total, page, pageSize });
        }

        [HttpGet("layout")]
        public async Task<IActionResult> Layout()
        {
            if (await ResolveClientIdAsync() is not int clientId) return Unauthorized();
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
                    f.options_json, f.group_id, f.assigned_system_group_key, f.sort_order
                }).ToListAsync();
            return Ok(new { groups, attributes });
        }

        [HttpPost("order")]
        public async Task<IActionResult> ReorderGroups([FromBody] ReorderGroupPageRequest request)
        {
            if (await ResolveClientIdAsync() is not int clientId) return Unauthorized();
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
            if (await ResolveClientIdAsync() is not int clientId) return Unauthorized();
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

        public class ReorderMixedAttributesRequest
        {
            public List<string> OrderedKeys { get; set; } = new();
        }

        [HttpPost("{groupId:int}/mixed-order")]
        public async Task<IActionResult> ReorderMixedAttributes(int groupId, [FromBody] ReorderMixedAttributesRequest request)
        {
            if (await ResolveClientIdAsync() is not int clientId) return Unauthorized();
            if (request?.OrderedKeys == null || request.OrderedKeys.Count == 0 ||
                request.OrderedKeys.Count != request.OrderedKeys.Distinct(StringComparer.Ordinal).Count())
                return BadRequest(new { message = "Invalid attribute order." });
            if (!await _context.crm_custom_field_groups.AnyAsync(g => g.id == groupId && g.client_id == clientId))
                return NotFound(new { message = "Group not found." });

            var strategy = _context.Database.CreateExecutionStrategy();
            return await strategy.ExecuteAsync<IActionResult>(async () =>
            {
                await using var transaction = await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable);
                var customFields = await _context.crm_custom_fields
                    .Where(f => f.client_id == clientId && f.group_id == groupId).ToListAsync();
                var groupKey = $"custom:{groupId}";
                var systemRows = new List<(string key, int order)>();
                var connection = _context.Database.GetDbConnection();
                await using (var read = connection.CreateCommand())
                {
                    read.Transaction = transaction.GetDbTransaction();
                    read.CommandText = @"SELECT field_key, sort_order FROM dbo.crm_system_field_order
WHERE client_id = @clientId AND assigned_group_key = @groupKey";
                    var client = read.CreateParameter(); client.ParameterName = "@clientId"; client.Value = clientId; read.Parameters.Add(client);
                    var group = read.CreateParameter(); group.ParameterName = "@groupKey"; group.Value = groupKey; read.Parameters.Add(group);
                    await using var reader = await read.ExecuteReaderAsync();
                    while (await reader.ReadAsync()) systemRows.Add((reader.GetString(0), reader.GetInt32(1)));
                }

                var customByKey = customFields.ToDictionary(f => $"c:{f.id}");
                var systemKeys = systemRows.Select(row => $"s:{row.key}").ToHashSet(StringComparer.Ordinal);
                var current = customFields.Select(f => (key: $"c:{f.id}", order: f.sort_order))
                    .Concat(systemRows.Select(row => (key: $"s:{row.key}", order: (long)row.order)))
                    .OrderBy(item => item.order).ThenBy(item => item.key, StringComparer.Ordinal)
                    .Select(item => item.key).ToList();
                if (request.OrderedKeys.Any(key => !customByKey.ContainsKey(key) && !systemKeys.Contains(key)))
                    return Conflict(new { message = "Attributes changed. Refresh and try again." });

                var selected = request.OrderedKeys.ToHashSet(StringComparer.Ordinal);
                var ordered = request.OrderedKeys.Concat(current.Where(key => !selected.Contains(key))).ToArray();
                await using var write = connection.CreateCommand();
                write.Transaction = transaction.GetDbTransaction();
                write.CommandText = @"UPDATE dbo.crm_system_field_order SET sort_order = @position
WHERE client_id = @clientId AND assigned_group_key = @groupKey AND field_key = @fieldKey";
                foreach (var (name, value) in new (string, object)[] {
                    ("@clientId", clientId), ("@groupKey", groupKey), ("@fieldKey", ""), ("@position", 0) })
                {
                    var parameter = write.CreateParameter(); parameter.ParameterName = name; parameter.Value = value;
                    write.Parameters.Add(parameter);
                }
                for (var index = 0; index < ordered.Length; index++)
                {
                    var position = (index + 1) * 1024;
                    if (customByKey.TryGetValue(ordered[index], out var field)) field.sort_order = position;
                    else
                    {
                        ((System.Data.Common.DbParameter)write.Parameters["@fieldKey"]!).Value = ordered[index][2..];
                        ((System.Data.Common.DbParameter)write.Parameters["@position"]!).Value = position;
                        await write.ExecuteNonQueryAsync();
                    }
                }
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
            if (await ResolveClientIdAsync() is not int clientId) return Unauthorized();
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
                        .ToListAsync();
                    if (addedFields.Count != addIds.Length)
                        return Conflict("An attribute is already in another group. Refresh and try again.");
                    var fieldsById = addedFields.ToDictionary(f => f.id);
                    foreach (var attributeId in addIds)
                    {
                        var field = fieldsById[attributeId];
                        if (field.group_id != null) continue;
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

        [HttpPost("attributes/move")]
        public async Task<IActionResult> MoveAttribute([FromBody] MoveCustomAttributeRequest request)
        {
            if (await ResolveClientIdAsync() is not int clientId) return Unauthorized();
            if (request == null || request.AttributeId <= 0 || request.DestinationGroupId < 0)
                return BadRequest(new { message = "Invalid attribute move." });
            var destinationSystemKey = request.DestinationSystemGroupKey?.Trim().ToLowerInvariant();
            if (destinationSystemKey != null && destinationSystemKey != "personal" && destinationSystemKey != "company")
                return BadRequest(new { message = "Invalid destination system group." });
            if (request.DestinationGroupId > 0 && destinationSystemKey != null)
                return BadRequest(new { message = "Choose one destination group." });

            var destinationId = request.DestinationGroupId == 0
                ? (int?)null : request.DestinationGroupId;
            var strategy = _context.Database.CreateExecutionStrategy();
            return await strategy.ExecuteAsync<IActionResult>(async () =>
            {
                await using var transaction = await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable);
                if (destinationId.HasValue && !await _context.crm_custom_field_groups.AnyAsync(
                    group => group.client_id == clientId && group.id == destinationId.Value))
                    return NotFound(new { message = "Destination group not found." });

                var attribute = await _context.crm_custom_fields.FirstOrDefaultAsync(
                    field => field.client_id == clientId && field.id == request.AttributeId);
                if (attribute == null) return NotFound(new { message = "Attribute not found." });
                if (attribute.group_id == destinationId && attribute.assigned_system_group_key == destinationSystemKey) return NoContent();

                var lastOrder = await _context.crm_custom_fields
                    .Where(field => field.client_id == clientId &&
                        (destinationSystemKey != null ? field.assigned_system_group_key == destinationSystemKey :
                         field.group_id == destinationId && field.assigned_system_group_key == null))
                    .MaxAsync(field => (long?)field.sort_order) ?? 0;
                attribute.group_id = destinationId;
                attribute.assigned_system_group_key = destinationSystemKey;
                attribute.sort_order = Math.Max(lastOrder, destinationSystemKey == null ? 0 : 1024) + 1024;
                await _context.SaveChangesAsync();
                await transaction.CommitAsync();
                return NoContent();
            });
        }

        [HttpPost("{id:int}/delete")]
        public async Task<IActionResult> Delete(int id)
        {
            if (await ResolveClientIdAsync() is not int clientId) return Unauthorized();
            var strategy = _context.Database.CreateExecutionStrategy();
            return await strategy.ExecuteAsync<IActionResult>(async () =>
            {
            await using var transaction = await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            var group = await _context.crm_custom_field_groups
                .FirstOrDefaultAsync(g => g.id == id && g.client_id == clientId);
            if (group == null) return NotFound("Group not found.");

            var connection = _context.Database.GetDbConnection();
            await using (var check = connection.CreateCommand())
            {
                check.Transaction = _context.Database.CurrentTransaction?.GetDbTransaction();
                check.CommandText = @"SELECT COUNT(1) FROM dbo.crm_system_field_order
WHERE client_id = @clientId AND assigned_group_key = @groupKey";
                var client = check.CreateParameter(); client.ParameterName = "@clientId"; client.Value = clientId; check.Parameters.Add(client);
                var groupKey = check.CreateParameter(); groupKey.ParameterName = "@groupKey"; groupKey.Value = $"custom:{id}"; check.Parameters.Add(groupKey);
                if (Convert.ToInt32(await check.ExecuteScalarAsync()) > 0)
                    return Conflict(new { message = "This group contains system fields. Move all its system fields to another group from the Attributes list before deleting it." });
            }

            await _context.crm_custom_fields
                .Where(f => f.client_id == clientId && f.group_id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(f => f.group_id, (int?)null));
            _context.crm_custom_field_groups.Remove(group);
            await _context.SaveChangesAsync();
            await transaction.CommitAsync();
            return NoContent();
            });
        }
        private record SystemField(string Key, string Name, string Type);
        private static readonly IReadOnlyDictionary<string, SystemField[]> Fields =
            new Dictionary<string, SystemField[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["personal"] = new[] {
                    new SystemField("firstName", "First name", "text"),
                    new SystemField("lastName", "Last name", "text"),
                    new SystemField("fullName", "Full name", "text"),
                    new SystemField("email", "Email", "email"),
                    new SystemField("linkedInUrl", "LinkedIn URL", "hyperlink")
                },
                ["company"] = new[] {
                    new SystemField("jobTitle", "Job title", "text"),
                    new SystemField("companyName", "Company name", "text"),
                    new SystemField("companyIndustry", "Company industry", "text"),
                    new SystemField("companyEmployeeCount", "Company employee count", "text"),
                    new SystemField("companyTelephone", "Company telephone", "text"),
                    new SystemField("countryOrAddress", "Country/address", "text"),
                    new SystemField("website", "Website", "hyperlink"),
                    new SystemField("companyLinkedInURL", "Company LinkedIn URL", "hyperlink")
                }
            };

        [HttpGet("system")]
        public async Task<IActionResult> ListSystemFields()
        {
            if (await ResolveClientIdAsync() is not int clientId) return Unauthorized();
            var saved = new Dictionary<string, (string groupKey, int order)>();
            var connection = _context.Database.GetDbConnection();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT group_key, field_key, sort_order, assigned_group_key FROM dbo.crm_system_field_order WHERE client_id = @clientId";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@clientId";
            parameter.Value = clientId;
            command.Parameters.Add(parameter);
            if (connection.State != ConnectionState.Open) await connection.OpenAsync();
            await using (var reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                    saved[reader.GetString(1)] = (reader.IsDBNull(3) ? reader.GetString(0).ToLowerInvariant() : reader.GetString(3).ToLowerInvariant(), reader.GetInt32(2));
            }
            var systemFields = Fields.SelectMany(group => group.Value.Select((field, index) => new {
                key = field.Key, name = field.Name, type = field.Type,
                groupKey = saved.GetValueOrDefault(field.Key, (group.Key, index)).Item1,
                order = saved.GetValueOrDefault(field.Key, (group.Key, index)).Item2
            })).ToArray();
            var customGroups = await _context.crm_custom_field_groups.AsNoTracking()
                .Where(group => group.client_id == clientId)
                .Select(group => new { group.id, group.name }).ToArrayAsync();
            var customCounts = await _context.crm_custom_fields.AsNoTracking()
                .Where(field => field.client_id == clientId && field.assigned_system_group_key != null)
                .GroupBy(field => field.assigned_system_group_key!)
                .Select(group => new { key = group.Key, count = group.Count() })
                .ToDictionaryAsync(item => item.key, item => item.count);
            var groups = Fields.Select(group => new {
                key = group.Key,
                name = group.Key == "personal" ? "Personal" : "Company",
                customAttributeCount = customCounts.GetValueOrDefault(group.Key),
                fields = systemFields.Where(field => field.groupKey == group.Key)
                    .OrderBy(field => field.order).ThenBy(field => field.key).ToArray()
            }).Concat(customGroups.Select(group => new {
                key = $"custom:{group.id}", name = group.name,
                customAttributeCount = 0,
                fields = systemFields.Where(field => field.groupKey == $"custom:{group.id}")
                    .OrderBy(field => field.order).ThenBy(field => field.key).ToArray()
            })).ToArray();
            return Ok(new { groups, fields = systemFields });
        }

        public class SaveOrderRequest
        {
            public List<string> OrderedKeys { get; set; } = new();
        }

        public class MoveSystemFieldsRequest
        {
            public string DestinationGroupKey { get; set; } = "";
            public List<string> FieldKeys { get; set; } = new();
        }

        [HttpPost("system/move")]
        public async Task<IActionResult> MoveSystemFields([FromBody] MoveSystemFieldsRequest request)
        {
            if (await ResolveClientIdAsync() is not int clientId) return Unauthorized();
            var destination = request?.DestinationGroupKey?.ToLowerInvariant() ?? "";
            if (!Fields.ContainsKey(destination) &&
                (!destination.StartsWith("custom:") || !int.TryParse(destination[7..], out var groupId) ||
                 !await _context.crm_custom_field_groups.AnyAsync(group => group.client_id == clientId && group.id == groupId)))
                return NotFound(new { message = "Destination group was not found." });
            var known = Fields.SelectMany(group => group.Value).Select(field => field.Key).ToHashSet(StringComparer.Ordinal);
            if (request?.FieldKeys == null || request.FieldKeys.Count == 0 ||
                request.FieldKeys.Count != request.FieldKeys.Distinct(StringComparer.Ordinal).Count() ||
                request.FieldKeys.Any(key => !known.Contains(key)))
                return BadRequest(new { message = "Invalid system fields." });

            var connection = _context.Database.GetDbConnection();
            if (connection.State != ConnectionState.Open) await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable);
            try
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = @"DECLARE @next int = (SELECT COALESCE(MAX(sort_order), @base - 1) + 1 FROM dbo.crm_system_field_order
WHERE client_id = @clientId AND assigned_group_key = @destination);
UPDATE dbo.crm_system_field_order SET assigned_group_key = @destination, sort_order = @next
WHERE client_id = @clientId AND field_key = @fieldKey;
IF @@ROWCOUNT = 0
    INSERT INTO dbo.crm_system_field_order (client_id, group_key, assigned_group_key, field_key, sort_order)
    VALUES (@clientId, @source, @destination, @fieldKey, @next);";
                foreach (var (name, value) in new (string, object)[] {
                    ("@clientId", clientId), ("@source", ""), ("@destination", destination),
                    ("@base", Fields.TryGetValue(destination, out var defaultFields) ? defaultFields.Length : 0),
                    ("@fieldKey", "") })
                {
                    var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value;
                    command.Parameters.Add(parameter);
                }
                foreach (var fieldKey in request.FieldKeys)
                {
                    ((System.Data.Common.DbParameter)command.Parameters["@fieldKey"]!).Value = fieldKey;
                    ((System.Data.Common.DbParameter)command.Parameters["@source"]!).Value = Fields.First(group => group.Value.Any(field => field.Key == fieldKey)).Key;
                    await command.ExecuteNonQueryAsync();
                }
                await transaction.CommitAsync();
                return NoContent();
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }

        [HttpPost("system/{groupKey}/order")]
        public async Task<IActionResult> SaveOrder(string groupKey, [FromBody] SaveOrderRequest request)
        {
            if (await ResolveClientIdAsync() is not int clientId) return Unauthorized();
            groupKey = groupKey.ToLowerInvariant();
            if (!Fields.ContainsKey(groupKey) &&
                (!groupKey.StartsWith("custom:") || !int.TryParse(groupKey[7..], out var groupId) ||
                 !await _context.crm_custom_field_groups.AnyAsync(group => group.client_id == clientId && group.id == groupId)))
                return NotFound();
            if (request?.OrderedKeys == null || request.OrderedKeys.Distinct().Count() != request.OrderedKeys.Count)
                return BadRequest(new { message = "Invalid system field order." });

            var connection = _context.Database.GetDbConnection();
            if (connection.State != ConnectionState.Open) await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable);
            try
            {
                await using (var current = connection.CreateCommand())
                {
                    current.Transaction = transaction;
                    current.CommandText = @"SELECT field_key FROM dbo.crm_system_field_order
WHERE client_id = @clientId AND (assigned_group_key = @groupKey OR (assigned_group_key IS NULL AND group_key = @groupKey))";
                    foreach (var (name, value) in new (string, object)[] { ("@clientId", clientId), ("@groupKey", groupKey) })
                    {
                        var parameter = current.CreateParameter(); parameter.ParameterName = name; parameter.Value = value;
                        current.Parameters.Add(parameter);
                    }
                    var expected = new HashSet<string>(StringComparer.Ordinal);
                    await using (var reader = await current.ExecuteReaderAsync())
                        while (await reader.ReadAsync()) expected.Add(reader.GetString(0));
                    foreach (var source in Fields)
                        if (source.Key == groupKey)
                            foreach (var field in source.Value)
                                if (!await SystemFieldHasRow(connection, transaction, clientId, field.Key)) expected.Add(field.Key);
                    if (!expected.SetEquals(request.OrderedKeys))
                        return Conflict(new { message = "System fields changed. Refresh and try again." });
                }
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = @"UPDATE dbo.crm_system_field_order SET sort_order = @sortOrder
WHERE client_id = @clientId AND field_key = @fieldKey;
IF @@ROWCOUNT = 0
    INSERT INTO dbo.crm_system_field_order (client_id, group_key, field_key, sort_order)
    VALUES (@clientId, @source, @fieldKey, @sortOrder);";
                foreach (var (name, value) in new (string, object)[] {
                    ("@clientId", clientId), ("@groupKey", groupKey.ToLowerInvariant()),
                    ("@source", ""), ("@fieldKey", ""), ("@sortOrder", 0) })
                {
                    var parameter = command.CreateParameter();
                    parameter.ParameterName = name;
                    parameter.Value = value;
                    command.Parameters.Add(parameter);
                }
                for (var index = 0; index < request.OrderedKeys.Count; index++)
                {
                    ((System.Data.Common.DbParameter)command.Parameters["@fieldKey"]!).Value = request.OrderedKeys[index];
                    ((System.Data.Common.DbParameter)command.Parameters["@sortOrder"]!).Value = index;
                    ((System.Data.Common.DbParameter)command.Parameters["@source"]!).Value = Fields.First(group => group.Value.Any(field => field.Key == request.OrderedKeys[index])).Key;
                    await command.ExecuteNonQueryAsync();
                }
                await transaction.CommitAsync();
                return NoContent();
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }

        private static async Task<bool> SystemFieldHasRow(System.Data.Common.DbConnection connection,
            System.Data.Common.DbTransaction transaction, int clientId, string fieldKey)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT COUNT(1) FROM dbo.crm_system_field_order WHERE client_id = @clientId AND field_key = @fieldKey";
            foreach (var (name, value) in new (string, object)[] { ("@clientId", clientId), ("@fieldKey", fieldKey) })
            {
                var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value;
                command.Parameters.Add(parameter);
            }
            return Convert.ToInt32(await command.ExecuteScalarAsync()) > 0;
        }
    }

    public class MoveCustomAttributeRequest
    {
        public int AttributeId { get; set; }
        // Zero represents the Ungrouped attributes card.
        public int DestinationGroupId { get; set; }
        public string? DestinationSystemGroupKey { get; set; }
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
