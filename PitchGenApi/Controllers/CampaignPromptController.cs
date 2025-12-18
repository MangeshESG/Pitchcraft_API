using Microsoft.AspNetCore.Mvc;
using PitchGenApi.Services;
using PitchGenApi.Model.DTOs;
using PitchGenApi.Models;
using Microsoft.EntityFrameworkCore;
using PitchGenApi.Database;
using System.Text.Json;
using System.Text.RegularExpressions;
using PitchGenApi.Model;

namespace PitchGenApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class CampaignPromptController : ControllerBase
    {
        private readonly CampaignPromptService _campaignService;
        private readonly AppDbContext _dbContext;

        public CampaignPromptController(
            CampaignPromptService campaignService,
            AppDbContext dbContext)
        {
            _campaignService = campaignService;
            _dbContext = dbContext;
        }

        #region Template Definition Endpoints (Shared Templates)

        // Create/Save a new template definition (admin operation)
        [HttpPost("template-definition/save")]
        public async Task<IActionResult> SaveTemplateDefinition([FromBody] SaveTemplateDefinitionRequest request)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request.TemplateName))
                    return BadRequest(new { Message = "Template name is required" });

                // Check if template name already exists
                var exists = await _dbContext.CampaignTemplateDefinitions
                    .AnyAsync(t => t.TemplateName == request.TemplateName);

                if (exists)
                    return BadRequest(new { Message = "A template with this name already exists" });

                var templateDef = new CampaignTemplateDefinition
                {
                    TemplateName = request.TemplateName,
                    AIInstructions = request.AIInstructions,
                    AIInstructionsForEdit = request.AIInstructionsForEdit,
                    PlaceholderList = request.PlaceholderList,
                    PlaceholderListExtensive = request.PlaceholderListExtensive,
                    MasterBlueprintUnpopulated = request.MasterBlueprintUnpopulated,
                    CreatedBy = request.CreatedBy,
                    CreatedAt = DateTime.UtcNow,
                    IsActive = true,
                    SearchURLCount = request.SearchURLCount,
                    SubjectInstructions = request.SubjectInstructions,
                    SelectedModel = request.SelectedModel   // ⭐ ADD THIS


                };

                _dbContext.CampaignTemplateDefinitions.Add(templateDef);
                await _dbContext.SaveChangesAsync();

                var placeholderKeys = ExtractPlaceholderKeys(
                    request.AIInstructions,
                    request.AIInstructionsForEdit,
                    request.PlaceholderList,
                    request.PlaceholderListExtensive,
                    request.MasterBlueprintUnpopulated
                );

                await SyncPlaceholderDefinitions(placeholderKeys);

                return Ok(new
                {
                    Success = true,
                    TemplateDefinitionId = templateDef.Id,
                    Message = "Template definition saved successfully"
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Error = ex.Message });
            }
        }

        // Get all template definitions
        [HttpGet("template-definitions")]
        public async Task<IActionResult> GetTemplateDefinitions([FromQuery] bool activeOnly = true)
        {
            try
            {
                var query = _dbContext.CampaignTemplateDefinitions.AsQueryable();

                if (activeOnly)
                    query = query.Where(t => t.IsActive);

                var definitions = await query
                    .OrderByDescending(t => t.CreatedAt)
                    .Select(t => new
                    {
                        t.Id,
                        t.TemplateName,
                        t.CreatedAt,
                        t.UpdatedAt,
                        t.IsActive,
                        UsageCount = t.CampaignTemplates.Count
                    })
                    .ToListAsync();

                return Ok(new { TemplateDefinitions = definitions });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Error = ex.Message });
            }
        }

        // Get specific template definition
        [HttpGet("template-definition/{id}")]
        public async Task<IActionResult> GetTemplateDefinition(int id)
        {
            try
            {
                var definition = await _dbContext.CampaignTemplateDefinitions
                    .FirstOrDefaultAsync(t => t.Id == id);

                if (definition == null)
                    return NotFound(new { Message = "Template definition not found" });

                return Ok(definition);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Error = ex.Message });
            }
        }

        // Update template definition
        [HttpPost("template-definition/update")]
        public async Task<IActionResult> UpdateTemplateDefinition([FromBody] UpdateTemplateDefinitionRequest request)
        {
            try
            {
                var definition = await _dbContext.CampaignTemplateDefinitions
                    .FirstOrDefaultAsync(t => t.Id == request.Id);

                if (definition == null)
                    return NotFound(new { Message = "Template definition not found" });

                definition.TemplateName = request.TemplateName;
                definition.AIInstructions = request.AIInstructions;
                definition.AIInstructionsForEdit = request.AIInstructionsForEdit;
                definition.PlaceholderList = request.PlaceholderList;
                definition.PlaceholderListExtensive = request.PlaceholderListExtensive;
                definition.MasterBlueprintUnpopulated = request.MasterBlueprintUnpopulated;
                definition.UpdatedAt = DateTime.UtcNow;
                definition.SearchURLCount = request.SearchURLCount;
                definition.SubjectInstructions = request.SubjectInstructions;
                definition.SelectedModel = request.SelectedModel;




                await _dbContext.SaveChangesAsync();

                // 🔁 Re-sync placeholders if instructions changed
                var placeholderKeys = ExtractPlaceholderKeys(
                    request.AIInstructions,
                    request.AIInstructionsForEdit,
                    request.PlaceholderList,
                    request.PlaceholderListExtensive,
                    request.MasterBlueprintUnpopulated
                );

                await SyncPlaceholderDefinitions(placeholderKeys);


                return Ok(new { Success = true, Message = "Template definition updated successfully" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Error = ex.Message });
            }
        }

        // Deactivate template definition
        [HttpPost("template-definition/{id}/deactivate")]
        public async Task<IActionResult> DeactivateTemplateDefinition(int id)
        {
            try
            {
                var definition = await _dbContext.CampaignTemplateDefinitions
                    .FirstOrDefaultAsync(t => t.Id == id);

                if (definition == null)
                    return NotFound(new { Message = "Template definition not found" });

                definition.IsActive = false;
                definition.UpdatedAt = DateTime.UtcNow;

                await _dbContext.SaveChangesAsync();

                return Ok(new { Success = true, Message = "Template definition deactivated" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Error = ex.Message });
            }
        }

        #endregion

        #region Client Campaign Templates Endpoints

        // Save client's filled campaign template
        // Save client's filled campaign template
        [HttpPost("template/save")]
        public async Task<IActionResult> SaveCampaignTemplate([FromBody] SaveCampaignTemplateRequest request)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request.ClientId))
                    return BadRequest(new { Message = "ClientId is required" });

                if (request.TemplateDefinitionId <= 0)
                    return BadRequest(new { Message = "Valid TemplateDefinitionId is required" });

                // Verify template definition exists
                var definitionExists = await _dbContext.CampaignTemplateDefinitions
                    .AnyAsync(t => t.Id == request.TemplateDefinitionId && t.IsActive);

                if (!definitionExists)
                    return BadRequest(new { Message = "Template definition not found or inactive" });

                // Create campaign template  ← UPDATED
                var campaignTemplate = new CampaignTemplate
                {
                    ClientId = request.ClientId,
                    TemplateDefinitionId = request.TemplateDefinitionId,
                    PlaceholderListWithValue = request.PlaceholderListWithValue,
                    CampaignBlueprint = request.CampaignBlueprint,
                    PlaceholderValues = request.PlaceholderValues != null
                        ? JsonSerializer.Serialize(request.PlaceholderValues)
                        : null,
                    SelectedModel = request.SelectedModel,
                    CreatedAt = DateTime.UtcNow,

                    // ⭐ NEW FIELDS START
                    SearchURLCount = request.SearchURLCount,
                    SubjectInstructions = request.SubjectInstructions
                    // ⭐ NEW FIELDS END
                };

                _dbContext.CampaignTemplates.Add(campaignTemplate);
                await _dbContext.SaveChangesAsync();

                // Save conversation if provided
                if (request.ConversationMessages != null && request.ConversationMessages.Count > 0)
                {
                    var conversation = new CampaignConversation
                    {
                        ClientId = request.ClientId,
                        CampaignTemplateId = campaignTemplate.Id,
                        ConversationData = JsonSerializer.Serialize(request.ConversationMessages),
                        Model = request.SelectedModel,
                        StartedAt = request.ConversationMessages[0].Timestamp,
                        CompletedAt = DateTime.UtcNow,
                        IsComplete = true
                    };

                    _dbContext.CampaignConversations.Add(conversation);
                    await _dbContext.SaveChangesAsync();
                }

                return Ok(new
                {
                    Success = true,
                    TemplateId = campaignTemplate.Id,
                    Message = "Campaign template saved successfully"
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Error = ex.Message });
            }
        }

        // Get client's campaign templates (list)
        [HttpGet("templates/{clientId}")]
        public async Task<IActionResult> GetCampaignTemplates(
            string clientId,
            [FromQuery] int pageSize = 20,
            [FromQuery] int pageNumber = 1)
        {
            try
            {
                var query = _dbContext.CampaignTemplates
                    .Include(t => t.TemplateDefinition)
                    .Where(t => t.ClientId == clientId)
                    .OrderByDescending(t => t.CreatedAt);

                var totalCount = await query.CountAsync();

                var templates = await query
                    .Skip((pageNumber - 1) * pageSize)
                    .Take(pageSize)
                    .Select(t => new
                    {
                        t.Id,
                        t.TemplateDefinitionId,
                        TemplateName = t.TemplateName, // ✅ instance name
                        TemplateDefinitionName = t.TemplateDefinition != null ? t.TemplateDefinition.TemplateName : "",
                        t.CreatedAt,
                        t.UpdatedAt,
                        t.SelectedModel,
                        HasConversation = t.Conversation != null
                    })
                    .ToListAsync();

                return Ok(new
                {
                    Templates = templates,
                    TotalCount = totalCount,
                    PageSize = pageSize,
                    PageNumber = pageNumber,
                    TotalPages = (int)Math.Ceiling(totalCount / (double)pageSize)
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Error = ex.Message });
            }
        }

        // Get specific campaign template with full details
        [HttpGet("campaign/{templateId}")]
        public async Task<IActionResult> GetCampaignTemplateDetails(int templateId)
        {
            try
            {
                var template = await _dbContext.CampaignTemplates
                    .Include(t => t.TemplateDefinition)
                    .FirstOrDefaultAsync(t => t.Id == templateId);

                if (template == null)
                    return NotFound(new { Message = "Template not found" });

                if (template.TemplateDefinition == null)
                    return StatusCode(500, new { Message = "Template definition is missing" });

                // -------------------------------
                // Deserialize placeholder values
                // -------------------------------
                Dictionary<string, string> placeholderValues =
                    string.IsNullOrEmpty(template.PlaceholderValues)
                        ? new Dictionary<string, string>()
                        : JsonSerializer.Deserialize<Dictionary<string, string>>(template.PlaceholderValues)
                          ?? new Dictionary<string, string>();

                // -------------------------------
                // Runtime placeholder replacement
                // -------------------------------
                string filledBlueprint = ApplyPlaceholders(
                    template.CampaignBlueprint,
                    placeholderValues
                );

                // -------------------------------
                // Build response
                // -------------------------------
                var result = new CampaignTemplateDetailResponse
                {
                    Id = template.Id,
                    ClientId = template.ClientId,
                    TemplateDefinitionId = template.TemplateDefinitionId,
                    TemplateName = template.TemplateDefinition.TemplateName,

                    AIInstructions = template.TemplateDefinition.AIInstructions,
                    AIInstructionsForEdit = template.TemplateDefinition.AIInstructionsForEdit,

                    PlaceholderList = template.TemplateDefinition.PlaceholderList,
                    PlaceholderListExtensive = template.TemplateDefinition.PlaceholderListExtensive,
                    MasterBlueprintUnpopulated = template.TemplateDefinition.MasterBlueprintUnpopulated,

                    PlaceholderListWithValue = template.PlaceholderListWithValue,

                    // ⭐ FILLED version for frontend
                    CampaignBlueprint = filledBlueprint,

                    // Raw values still exposed if frontend needs them
                    PlaceholderValues = placeholderValues,

                    SelectedModel = template.SelectedModel,
                    CreatedAt = template.CreatedAt,
                    UpdatedAt = template.UpdatedAt,
                    SearchURLCount = template.SearchURLCount,
                    SubjectInstructions = template.SubjectInstructions,
                    Conversation = null
                };

                return Ok(result);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Error = ex.Message });
            }
        }

        // Update client's campaign template
        [HttpPost("template/update")]
        public async Task<IActionResult> UpdateCampaignTemplate([FromBody] UpdateCampaignTemplateRequest request)
        {
            try
            {
                var template = await _dbContext.CampaignTemplates
                    .FirstOrDefaultAsync(t => t.Id == request.Id);

                if (template == null)
                    return NotFound(new { Message = "Template not found" });

                // Update only client-specific fields
                if (!string.IsNullOrEmpty(request.PlaceholderListWithValue))
                    template.PlaceholderListWithValue = request.PlaceholderListWithValue;



                if (request.PlaceholderValues != null)
                    template.PlaceholderValues = JsonSerializer.Serialize(request.PlaceholderValues);

                if (!string.IsNullOrEmpty(request.SelectedModel))
                    template.SelectedModel = request.SelectedModel;


                // ⭐ NEW FIELDS — minimal patch
                if (request.SearchURLCount.HasValue)
                    template.SearchURLCount = request.SearchURLCount;

                if (request.SubjectInstructions != null)
                    template.SubjectInstructions = request.SubjectInstructions;


                template.UpdatedAt = DateTime.UtcNow;

                await _dbContext.SaveChangesAsync();

                return Ok(new { Success = true, Message = "Template updated successfully" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Error = ex.Message });
            }
        }

        // Delete client's campaign template
        [HttpPost("template/{templateId}/delete")]
        public async Task<IActionResult> DeleteCampaignTemplate(int templateId)
        {
            try
            {
                var template = await _dbContext.CampaignTemplates
                    .Include(t => t.Conversation)
                    .FirstOrDefaultAsync(t => t.Id == templateId);

                if (template == null)
                    return NotFound(new { Message = "Template not found" });

                // Delete conversation if exists
                if (template.Conversation != null)
                    _dbContext.CampaignConversations.Remove(template.Conversation);

                _dbContext.CampaignTemplates.Remove(template);
                await _dbContext.SaveChangesAsync();

                return Ok(new { Success = true, Message = "Template deleted successfully" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Error = ex.Message });
            }
        }

        #endregion

        #region Chat Endpoints

        [HttpPost("chat")]
        public async Task<IActionResult> Chat([FromBody] ChatRequestDto request)
        {
            if (string.IsNullOrWhiteSpace(request.UserId))
                return BadRequest(new { Message = "UserId is required" });

            var model = string.IsNullOrWhiteSpace(request.Model) ? "gpt-5" : request.Model;

            var result = await _campaignService.ProcessChatAsync(
                request.UserId,
                request.Message ?? "",
                request.SystemPrompt ?? "",
                model
            );

            return Ok(new { Response = result });
        }

        [HttpGet("history/{userId}")]
        public IActionResult GetChatHistory(string userId)
        {
            var history = _campaignService.GetChatHistory(userId);
            if (history == null)
                return NotFound(new { Message = "No chat history found for this user" });

            return Ok(new { History = history });
        }

        [HttpPost("history/{userId}/clear")]
        public IActionResult ClearChatHistory(string userId)
        {
            _campaignService.ClearChatHistory(userId);
            return Ok(new { Message = "Chat history cleared" });
        }


        [HttpPost("example/generate")]
        public async Task<IActionResult> RegenerateExample(
            [FromBody] GenerateExampleOutputRequest req)
        {
            if (req.CampaignTemplateId <= 0)
                return BadRequest(new { Message = "Valid CampaignTemplateId required" });

            var template = await _dbContext.CampaignTemplates
                .Include(t => t.TemplateDefinition)
                .FirstOrDefaultAsync(t => t.Id == req.CampaignTemplateId);

            if (template == null)
                return NotFound(new { Message = "Template not found" });

            // 1️⃣ Load conversation placeholders from DB
            var persistedVals = string.IsNullOrEmpty(template.PlaceholderValues)
                ? new Dictionary<string, string>()
                : JsonSerializer.Deserialize<Dictionary<string, string>>(template.PlaceholderValues)
                  ?? new Dictionary<string, string>();

            // 2️⃣ Clone so DB data is never mutated
            var runtimeVals = new Dictionary<string, string>(persistedVals);

            // 3️⃣ Merge runtime placeholders from frontend
            if (req.PlaceholderValues?.Count > 0)
            {
                foreach (var pair in req.PlaceholderValues)
                    runtimeVals[pair.Key] = pair.Value;
            }

            string master = template.TemplateDefinition.MasterBlueprintUnpopulated ?? "";

            // 4️⃣ Generate email
            var rawResult = await _campaignService.GenerateExampleOutputAsync(
                runtimeVals,
                master,
                req.Model ?? "gpt-4o"
            );

            string filledTemplate = "";
            string html = rawResult ?? "";

            // 5️⃣ Extract filled template + HTML
            if (!string.IsNullOrEmpty(rawResult))
            {
                int s = rawResult.IndexOf("__FILLED_TEMPLATE_START__");
                int e = rawResult.IndexOf("__FILLED_TEMPLATE_END__");

                if (s >= 0 && e > s)
                {
                    filledTemplate = rawResult.Substring(
                        s + "__FILLED_TEMPLATE_START__".Length,
                        e - (s + "__FILLED_TEMPLATE_START__".Length)
                    );

                    html = rawResult.Substring(
                        e + "__FILLED_TEMPLATE_END__".Length
                    );
                }
            }

            // 6️⃣ Save ONLY example output
            if (!string.IsNullOrEmpty(html))
            {
                template.ExampleOutput = html;
                template.UpdatedAt = DateTime.UtcNow;
                await _dbContext.SaveChangesAsync();
            }

            return Ok(new
            {
                Success = true,
                ExampleOutput = html,
                FilledTemplate = filledTemplate
            });
        }

        // =====================================================
        //  Create campaign instance (called before first chat)
        // =====================================================
        [HttpPost("campaign/start")]
        public async Task<IActionResult> StartCampaign([FromBody] StartCampaignRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.ClientId))
                return BadRequest(new { Message = "ClientId required" });

            if (req.TemplateDefinitionId <= 0)
                return BadRequest(new { Message = "Valid TemplateDefinitionId required" });

            if (string.IsNullOrWhiteSpace(req.TemplateName))
                return BadRequest(new { Message = "Template name required" });

            // Load template definition
            var templateDef = await _dbContext.CampaignTemplateDefinitions
                .FirstOrDefaultAsync(t => t.Id == req.TemplateDefinitionId);

            if (templateDef == null)
                return BadRequest(new { Message = "Template definition not found" });

            // =====================================================
            // ⭐ Create new campaign instance
            // =====================================================
            var campaign = new CampaignTemplate
            {
                ClientId = req.ClientId,
                TemplateDefinitionId = req.TemplateDefinitionId,
                TemplateName = req.TemplateName,

                // ✅ MASTER → INSTANCE COPY (CRITICAL)
                CampaignBlueprint = templateDef.MasterBlueprintUnpopulated,

                // ✅ clean initial state
                PlaceholderValues = "{}",
                PlaceholderListWithValue = "",

                SelectedModel = templateDef.SelectedModel,
                SearchURLCount = templateDef.SearchURLCount,
                SubjectInstructions = templateDef.SubjectInstructions,

                CreatedAt = DateTime.UtcNow
            };


            _dbContext.CampaignTemplates.Add(campaign);
            await _dbContext.SaveChangesAsync();

            // =====================================================
            // ⭐ Create initial conversation row (Mode = NEW)
            // =====================================================
            var conversation = new CampaignConversation
            {
                ClientId = req.ClientId,
                CampaignTemplateId = campaign.Id,
                StartedAt = DateTime.UtcNow,

                // NEW FIELDS
                Mode = "new",
                EditNumber = 0
            };

            _dbContext.CampaignConversations.Add(conversation);
            await _dbContext.SaveChangesAsync();

            // =====================================================
            // ⭐ Create in-memory session for live chat
            // =====================================================
            CampaignPromptService._sessions[req.ClientId] = new CampaignPromptService.CampaignSession
            {
                UserId = req.ClientId,
                CampaignTemplateId = campaign.Id,
                Messages = new List<Dictionary<string, string>>
                {
                    new()
                    {
                        { "role", "system" },
                        { "content", templateDef.AIInstructions
                                     ?? templateDef.PlaceholderListExtensive
                                     ?? templateDef.PlaceholderList
                                     ?? "" }
                    }
                }
            };



            // =====================================================
            // Return response
            // =====================================================
            return Ok(new
            {
                Success = true,
                CampaignId = campaign.Id,
                TemplateName = campaign.TemplateName,
                TemplateDefinitionId = campaign.TemplateDefinitionId,
                Message = $"Template '{req.TemplateName}' created. Ready to start conversation..."
            });
        }
        #endregion



        [HttpPost("edit/start")]
        public async Task<IActionResult> StartEditConversation([FromBody] StartEditConversationRequest req)
        {
            var result = await _campaignService.StartEditModeAsync(req);
            return Ok(new { response = result });
        }

        [HttpPost("edit/chat")]
        public async Task<IActionResult> EditChat([FromBody] EditChatRequest req)
        {
            var result = await _campaignService.ContinueEditModeAsync(req);
            return Ok(new { response = result });
        }

        private const string ExampleOutputKey = "example_output";

        [HttpPost("template/update-placeholders")]
        public async Task<IActionResult> UpdatePlaceholders(
            [FromBody] UpdatePlaceholdersRequest req)
        {
            if (req.TemplateId <= 0)
                return BadRequest(new { Message = "TemplateId required" });

            var campaign = await _dbContext.CampaignTemplates
                .Include(c => c.TemplateDefinition)
                .FirstOrDefaultAsync(c => c.Id == req.TemplateId);

            if (campaign == null)
                return NotFound(new { Message = "Campaign not found" });

            if (campaign.TemplateDefinition == null)
                return StatusCode(500, new { Message = "Template definition missing" });

            var existing = string.IsNullOrEmpty(campaign.PlaceholderValues)
                ? new Dictionary<string, string>()
                : JsonSerializer.Deserialize<Dictionary<string, string>>(campaign.PlaceholderValues)
                  ?? new Dictionary<string, string>();

            if (req.PlaceholderValues != null)
            {
                foreach (var kv in req.PlaceholderValues)
                {
                    // 🚫 SKIP runtime-only placeholders
                    if (RuntimeOnlyPlaceholders.Contains(kv.Key))
                        continue;

                    // ✅ Store allowed placeholders
                    existing[kv.Key] = kv.Value;

                    // ✅ Sync example_output
                    if (kv.Key.Equals("example_output", StringComparison.OrdinalIgnoreCase))
                    {
                        campaign.ExampleOutput = kv.Value;
                    }
                }
            }

            // Save JSON (ONLY allowed placeholders)
            campaign.PlaceholderValues = JsonSerializer.Serialize(existing);

            // Human-readable list (ONLY allowed placeholders)
            campaign.PlaceholderListWithValue = string.Join(
                "\n",
                existing.Select(kv => $"{{{kv.Key}}} = {kv.Value}")
            );

            // ✅ Blueprint ALWAYS stays unpopulated
            campaign.CampaignBlueprint =
                campaign.TemplateDefinition.MasterBlueprintUnpopulated;

            campaign.UpdatedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync();

            return Ok(new
            {
                Success = true,
                Message = "Placeholders saved (runtime placeholders excluded)"
            });
        }

        private static string ApplyPlaceholders(

            string blueprint,
            Dictionary<string, string>? values)
        {
            if (string.IsNullOrEmpty(blueprint) || values == null || values.Count == 0)
                return blueprint ?? "";

            string result = blueprint;

            foreach (var (key, value) in values)
            {
                result = Regex.Replace(
                    result,
                    $"{{{Regex.Escape(key)}}}",
                    value ?? "",
                    RegexOptions.IgnoreCase
                );
            }

            return result;
        }

        private static readonly HashSet<string> RuntimeOnlyPlaceholders =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "full_name",
            "first_name",
            "last_name",
            "job_title",
            "location",
            "linkedin_url",
            "company_name",
            "company_name_friendly",
            "website"
        };


        // ============================================
        // 🎨 ELEMENTS TAB – PLACEHOLDERS WITH METADATA
        // ============================================
        [HttpGet("placeholders/by-campaign/{campaignId}")]
        public async Task<IActionResult> GetPlaceholdersForCampaign(int campaignId)
        {
            var campaign = await _dbContext.CampaignTemplates
                .FirstOrDefaultAsync(c => c.Id == campaignId);

            if (campaign == null)
                return NotFound(new { Message = "Campaign not found" });

            var values = string.IsNullOrEmpty(campaign.PlaceholderValues)
                ? new Dictionary<string, string>()
                : JsonSerializer.Deserialize<Dictionary<string, string>>(campaign.PlaceholderValues)
                  ?? new Dictionary<string, string>();

            var placeholders = await _dbContext.PlaceholderDefinitions
                .OrderBy(p => p.Category)
                .ThenBy(p => p.FriendlyName)
                .Select(p => new
                {
                    key = p.PlaceholderKey,
                    friendlyName = p.FriendlyName,
                    description = p.Description,
                    category = p.Category,
                    inputType = p.InputType,
                    uiSize = p.UiSize,
                    expandable = p.IsExpandable,
                    isRuntimeOnly = p.IsRuntimeOnly,
                    value = values.ContainsKey(p.PlaceholderKey)
                        ? values[p.PlaceholderKey]
                        : ""
                })
                .ToListAsync();

            return Ok(placeholders);
        }

        // ============================================
        // 🔁 PLACEHOLDER DEFINITION SYNC HELPERS
        // ============================================

        private static HashSet<string> ExtractPlaceholderKeys(params string?[] sources)
        {
            var regex = new Regex(@"\{([^}]+)\}");
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var src in sources)
            {
                if (string.IsNullOrWhiteSpace(src)) continue;

                foreach (Match m in regex.Matches(src))
                    result.Add(m.Groups[1].Value.Trim());
            }

            return result;
        }

        private async Task SyncPlaceholderDefinitions(IEnumerable<string> keys)
        {
            var existingKeys = (await _dbContext.PlaceholderDefinitions
                    .Select(p => p.PlaceholderKey)
                    .ToListAsync())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);



            foreach (var key in keys)
            {
                if (existingKeys.Contains(key))
                    continue;

                _dbContext.PlaceholderDefinitions.Add(new PlaceholderDefinition
                {
                    PlaceholderKey = key,
                    FriendlyName = Regex.Replace(key, "_+", " ")
                                        .Trim()
                                        .ToUpperInvariant(),
                    Category = InferCategory(key),
                    InputType = InferInputType(key),
                    UiSize = InferUiSize(key),
                    IsExpandable = key.Contains("example") || key.Contains("output"),
                    IsRichText = key.Contains("example") || key.Contains("output"),
                    IsRuntimeOnly = RuntimeOnlyPlaceholders.Contains(key),
                    CreatedAt = DateTime.UtcNow
                });
            }

            await _dbContext.SaveChangesAsync();
        }

        private static string InferCategory(string key)
        {
            if (RuntimeOnlyPlaceholders.Contains(key)) return "Contact";
            if (key.Contains("search")) return "Search";
            if (key.Contains("example") || key.Contains("output")) return "Output";
            if (key.Contains("vendor")) return "Vendor";
            return "Custom";
        }

        private static string InferInputType(string key)
        {
            if (key.Contains("example") || key.Contains("output")) return "richtext";
            if (key.Contains("instruction")) return "textarea";
            return "text";
        }

        private static string InferUiSize(string key)
        {
            if (key.Contains("example") || key.Contains("output")) return "xl";
            return "md";
        }


    }
}