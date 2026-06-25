using Microsoft.AspNetCore.Mvc;
using PitchGenApi.Services;
using PitchGenApi.Model.DTOs;
using PitchGenApi.Models;
using Microsoft.EntityFrameworkCore;
using PitchGenApi.Database;
using System.Text.Json;
using System.Text.RegularExpressions;
using PitchGenApi.Model;
using PitchGenApi.Interfaces;
using PitchGenApi.Model.DTOs;
using System.Net;




namespace PitchGenApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]


    public class CampaignPromptController : ControllerBase
    {
        private readonly CampaignPromptService _campaignService;
        private readonly AppDbContext _dbContext;
        private readonly IPitchService _pitchService;
        private readonly ContactRepository _contactRepository;
        private readonly INoteRepository _noteRepository;
        private readonly DeepSeekPitchService _deepSeekService;



        public CampaignPromptController(
            CampaignPromptService campaignService,
            AppDbContext dbContext,
            IPitchService pitchService,
            ContactRepository contactRepository,
            INoteRepository noteRepository,
            DeepSeekPitchService deepSeekService)
        {
            _campaignService = campaignService;
            _dbContext = dbContext;
            _pitchService = pitchService;
            _contactRepository = contactRepository;
            _noteRepository = noteRepository;
            _deepSeekService = deepSeekService;
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
                    WebSearchInstructions = request.WebSearchInstructions,
                    SelectedModel = request.SelectedModel
                };


                _dbContext.CampaignTemplateDefinitions.Add(templateDef);
                await _dbContext.SaveChangesAsync();

                var placeholderKeys = ExtractPlaceholderKeys(

                    request.PlaceholderList
                );

                await SyncPlaceholderDefinitions(placeholderKeys, templateDef.Id);

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

                if (!string.IsNullOrWhiteSpace(request.SubjectInstructions))
                    definition.SubjectInstructions = request.SubjectInstructions;

                // Allow clearing the web search instructions
                definition.WebSearchInstructions = request.WebSearchInstructions;

                definition.SelectedModel = request.SelectedModel;





                await _dbContext.SaveChangesAsync();

                // 🔁 Re-sync placeholders if instructions changed
                var placeholderKeys = ExtractPlaceholderKeys(
                    request.PlaceholderList
                );

                await SyncPlaceholderDefinitions(placeholderKeys, request.Id);


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
        public async Task<IActionResult> SaveCampaignTemplate(
            [FromBody] SaveCampaignTemplateRequest request)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request.ClientId))
                    return BadRequest(new { Message = "ClientId is required" });

                if (request.TemplateDefinitionId <= 0)
                    return BadRequest(new { Message = "Valid TemplateDefinitionId is required" });

                // ✅ LOAD template definition (FIX)
                var templateDef = await _dbContext.CampaignTemplateDefinitions
                    .FirstOrDefaultAsync(t =>
                        t.Id == request.TemplateDefinitionId &&
                        t.IsActive
                    );

                if (templateDef == null)
                    return BadRequest(new { Message = "Template definition not found or inactive" });

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

                    SearchURLCount = request.SearchURLCount,

                    // ✅ SAFE subject instruction handling
                    SubjectInstructions =
                        !string.IsNullOrWhiteSpace(request.SubjectInstructions)
                            ? request.SubjectInstructions
                            : templateDef.SubjectInstructions ?? ""
                };

                _dbContext.CampaignTemplates.Add(campaignTemplate);
                await _dbContext.SaveChangesAsync();

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
                        TemplateName = t.TemplateName,
                        TemplateDefinitionName = t.TemplateDefinition != null ? t.TemplateDefinition.TemplateName : "",
                        t.CreatedAt,
                        t.UpdatedAt,
                        t.SelectedModel,
                        HasConversation = _dbContext.CampaignConversations.Any(c => c.CampaignTemplateId == t.Id)
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
                    template.TemplateDefinition.MasterBlueprintUnpopulated,
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
                    WebSearchInstructions = template.TemplateDefinition.WebSearchInstructions,


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

                if (!string.IsNullOrWhiteSpace(request.SubjectInstructions))
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
                    .FirstOrDefaultAsync(t => t.Id == templateId);

                if (template == null)
                    return NotFound(new { Message = "Template not found" });

                // Remove ALL related conversations
                var conversations = await _dbContext.CampaignConversations
                    .Where(c => c.CampaignTemplateId == templateId)
                    .ToListAsync();

                if (conversations.Count > 0)
                    _dbContext.CampaignConversations.RemoveRange(conversations);

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
                model,
                request.ImageUrl,
                request.CampaignTemplateId
            );

            return Ok(new { Response = result });
        }

        [HttpGet("history/{userId}")]
        public async Task<IActionResult> GetChatHistory(
            string userId,
            [FromQuery] int? campaignTemplateId = null)
        {
            var history = await _campaignService.GetChatHistoryAsync(userId, campaignTemplateId);

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

            if (template.TemplateDefinition == null)
                return StatusCode(500, new { Message = "Template definition missing" });

            // 1️⃣ Load persisted placeholder values
            var persistedVals = string.IsNullOrEmpty(template.PlaceholderValues)
                ? new Dictionary<string, string>()
                : JsonSerializer.Deserialize<Dictionary<string, string>>(template.PlaceholderValues)
                  ?? new Dictionary<string, string>();

            // 2️⃣ Clone to avoid DB mutation
            var runtimeVals = new Dictionary<string, string>(persistedVals);

            // 3️⃣ Merge runtime placeholders from frontend
            if (req.PlaceholderValues?.Count > 0)
            {
                foreach (var pair in req.PlaceholderValues)
                    runtimeVals[pair.Key] = pair.Value;
            }

            // 4️⃣ Get master blueprint
            string masterBlueprint =
                template.TemplateDefinition.MasterBlueprintUnpopulated ?? "";

            if (string.IsNullOrWhiteSpace(masterBlueprint))
                return StatusCode(500, new { Message = "Master blueprint is empty" });

            // 5️⃣ Generate FILLED TEMPLATE (placeholder replacement via AI)
            var rawResult = await _campaignService.GenerateExampleOutputAsync(
                runtimeVals,
                masterBlueprint,
                req.Model ?? "gpt-5.1"
            );

            if (!string.IsNullOrWhiteSpace(rawResult))
            {
                int clientId = int.Parse(req.UserId);
                await _contactRepository.CreditDeduction(clientId);

            }

            if (string.IsNullOrWhiteSpace(rawResult))
                return StatusCode(500, new { Message = "Failed to generate filled template" });

            // 6️⃣ Extract filled template
            string filledTemplate = "";
            int start = rawResult.IndexOf("__FILLED_TEMPLATE_START__");
            int end = rawResult.IndexOf("__FILLED_TEMPLATE_END__");

            if (start >= 0 && end > start)
            {
                filledTemplate = rawResult.Substring(
                    start + "__FILLED_TEMPLATE_START__".Length,
                    end - (start + "__FILLED_TEMPLATE_START__".Length)
                ).Trim();
            }

            if (string.IsNullOrWhiteSpace(filledTemplate))
                return StatusCode(500, new { Message = "Filled template extraction failed" });

            // 7️⃣ Generate EXAMPLE EMAIL using EXISTING PitchService
            var pitchResult = await GeneratePitchByProviderAsync(
                new EnquiryRequest
                {
                    Prompt = filledTemplate,
                    ScrappedData = "Generate a professional example email",
                    ModelName = req.Model
                }
            );

            if (!pitchResult.IsSuccess || string.IsNullOrWhiteSpace(pitchResult.Content))
            {
                return StatusCode(500, new
                {
                    Message = "Example email generation failed",
                    Error = pitchResult.Content
                });
            }

            // 8️⃣ Save ONLY example output
            template.ExampleOutput = pitchResult.Content;
            template.UpdatedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync();

            return Ok(new
            {
                Success = true,
                ExampleOutput = pitchResult.Content,
                FilledTemplate = filledTemplate,
                Usage = new
                {
                    promptTokens = pitchResult.PromptTokens,
                    completionTokens = pitchResult.CompletionTokens,
                    searchTokens = pitchResult.SearchTokens,   // add this line
                    totalTokens = pitchResult.TotalTokens,
                    cost = pitchResult.CurrentCost
                }
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

                SelectedModel = "deepseek-v4-flash",
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
            // Remove any previous in-memory session for this user
            CampaignPromptService._sessions.Remove(req.ClientId);

            // Create a fresh session
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
            try
            {
                var result = await _campaignService.ContinueEditModeAsync(req);
                return Ok(new { response = result });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
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
            // 1️⃣ Load campaign
            var campaign = await _dbContext.CampaignTemplates
                .FirstOrDefaultAsync(c => c.Id == campaignId);

            if (campaign == null)
                return NotFound(new { Message = "Campaign not found" });

            // 2️⃣ Deserialize campaign placeholder values
            var values = string.IsNullOrWhiteSpace(campaign.PlaceholderValues)
                ? new Dictionary<string, string>()
                : JsonSerializer.Deserialize<Dictionary<string, string>>(campaign.PlaceholderValues)
                  ?? new Dictionary<string, string>();

            // 3️⃣ Load placeholder definitions FROM DATABASE (NO dictionary logic here)
            var definitions = await _dbContext.PlaceholderDefinitions
                .OrderBy(p => p.CategorySequence)
                .ThenBy(p => p.PlaceholderSequence)
                .ThenBy(p => p.FriendlyName)
                .ToListAsync();

            // 4️⃣ Apply fallback logic IN MEMORY (this fixes the error)
            var placeholders = definitions.Select(p =>
            {
                values.TryGetValue(p.PlaceholderKey, out var storedValue);

                return new
                {
                    key = p.PlaceholderKey,
                    friendlyName = p.FriendlyName,
                    description = p.Description,
                    category = p.Category,
                    inputType = p.InputType,
                    uiSize = p.UiSize,
                    expandable = p.IsExpandable,
                    isRuntimeOnly = p.IsRuntimeOnly,

                    // ⭐ NEW FIELDS
                    defaultValue = p.DefaultValue,
                    helpLink = p.HelpLink,

                    categorySequence = p.CategorySequence,
                    placeholderSequence = p.PlaceholderSequence,

                    value = !string.IsNullOrEmpty(storedValue)
                        ? storedValue
                        : (p.DefaultValue ?? "")
                };
            }).ToList();


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

        private async Task SyncPlaceholderDefinitions(
            IEnumerable<string> keys,
            int templateDefinitionId
        )
        {
            var existingKeys = await _dbContext.PlaceholderDefinitions
                .Where(p => p.TemplateDefinitionId == templateDefinitionId)
                .Select(p => p.PlaceholderKey)
                .ToListAsync();

            var existingSet = existingKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var key in keys)
            {
                if (existingSet.Contains(key))
                    continue;

                _dbContext.PlaceholderDefinitions.Add(new PlaceholderDefinition
                {
                    TemplateDefinitionId = templateDefinitionId, // ⭐⭐ THIS WAS MISSING
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
                    CategorySequence = 999,
                    PlaceholderSequence = 999,
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

        // ============================================
        // 💾 SAVE PLACEHOLDER DEFINITIONS (ADMIN)
        // ============================================
        [HttpPost("placeholders/save")]
        public async Task<IActionResult> SavePlaceholderDefinitions(
            [FromBody] SavePlaceholderDefinitionsRequest req)
        {
            if (req.TemplateDefinitionId <= 0)
                return BadRequest(new { Message = "TemplateDefinitionId required" });

            var existing = await _dbContext.PlaceholderDefinitions
                .Where(p => p.TemplateDefinitionId == req.TemplateDefinitionId)
                .ToListAsync();

            var map = existing.ToDictionary(
                p => p.PlaceholderKey,
                StringComparer.OrdinalIgnoreCase
            );

            foreach (var p in req.Placeholders)
            {
                if (map.TryGetValue(p.PlaceholderKey, out var entity))
                {
                    // ==============================
                    // UPDATE EXISTING PLACEHOLDER
                    // ==============================
                    entity.FriendlyName = p.FriendlyName;
                    entity.Description = p.Description;
                    entity.Category = p.Category;
                    entity.InputType = p.InputType;
                    entity.UiSize = p.UiSize;
                    entity.IsRuntimeOnly = p.IsRuntimeOnly;
                    entity.IsExpandable = p.IsExpandable;
                    entity.IsRichText = p.IsRichText;
                    entity.DefaultValue = p.DefaultValue;
                    entity.HelpLink = p.HelpLink;

                    entity.OptionsJson = p.Options != null
                        ? JsonSerializer.Serialize(p.Options)
                        : null;

                    // ⭐⭐ NEW: SAVE ORDER
                    entity.CategorySequence = p.CategorySequence;
                    entity.PlaceholderSequence = p.PlaceholderSequence;

                    entity.UpdatedAt = DateTime.UtcNow;
                }
                else
                {
                    // ==============================
                    // INSERT NEW PLACEHOLDER
                    // ==============================
                    _dbContext.PlaceholderDefinitions.Add(new PlaceholderDefinition
                    {
                        TemplateDefinitionId = req.TemplateDefinitionId,
                        PlaceholderKey = p.PlaceholderKey,
                        FriendlyName = p.FriendlyName,
                        Description = p.Description,
                        Category = p.Category,
                        InputType = p.InputType,
                        UiSize = p.UiSize,
                        IsRuntimeOnly = p.IsRuntimeOnly,
                        IsExpandable = p.IsExpandable,
                        IsRichText = p.IsRichText,
                        DefaultValue = p.DefaultValue,
                        HelpLink = p.HelpLink,
                        OptionsJson = p.Options != null
                            ? JsonSerializer.Serialize(p.Options)
                            : null,

                        // ⭐⭐ NEW: SAVE ORDER
                        CategorySequence = p.CategorySequence > 0 ? p.CategorySequence : 999,
                        PlaceholderSequence = p.PlaceholderSequence > 0 ? p.PlaceholderSequence : 999,

                        CreatedAt = DateTime.UtcNow
                    });
                }
            }

            await _dbContext.SaveChangesAsync();

            return Ok(new
            {
                Success = true,
                Message = "Placeholder definitions updated"
            });
        }



        // ============================================
        // 📥 GET PLACEHOLDER DEFINITIONS BY TEMPLATE
        // ============================================

        [HttpGet("placeholders/by-template/{templateDefinitionId}")]
        public async Task<IActionResult> GetPlaceholdersByTemplate(int templateDefinitionId)
        {
            if (templateDefinitionId <= 0)
                return BadRequest(new { Message = "TemplateDefinitionId required" });

            // ⭐ Correct sorting by sequences
            var entities = await _dbContext.PlaceholderDefinitions
                .Where(p => p.TemplateDefinitionId == templateDefinitionId)
                .OrderBy(p => p.CategorySequence)
                .ThenBy(p => p.PlaceholderSequence)
                .ToListAsync();

            var result = entities.Select(p => new
            {
                placeholderKey = p.PlaceholderKey,
                friendlyName = p.FriendlyName,
                description = p.Description,
                category = p.Category,
                inputType = p.InputType,
                uiSize = p.UiSize,
                isExpandable = p.IsExpandable,
                isRichText = p.IsRichText,
                isRuntimeOnly = p.IsRuntimeOnly,
                defaultValue = p.DefaultValue,
                helpLink = p.HelpLink,

                // ⭐ MUST INCLUDE THESE OR FRONTEND BREAKS
                categorySequence = p.CategorySequence,
                placeholderSequence = p.PlaceholderSequence,

                options = string.IsNullOrWhiteSpace(p.OptionsJson)
                    ? new List<string>()
                    : JsonSerializer.Deserialize<List<string>>(p.OptionsJson)
            });

            return Ok(result);
        }

        // ============================================
        // ❌ DELETE PLACEHOLDER DEFINITION (POST)
        // ============================================
        [HttpPost("placeholders/delete")]
        public async Task<IActionResult> DeletePlaceholderDefinition(
            [FromBody] DeletePlaceholderDefinitionRequest req)
        {
            if (req.TemplateDefinitionId <= 0 || string.IsNullOrWhiteSpace(req.PlaceholderKey))
                return BadRequest(new { Message = "Invalid request data" });

            var entity = await _dbContext.PlaceholderDefinitions
                .FirstOrDefaultAsync(p =>
                    p.TemplateDefinitionId == req.TemplateDefinitionId &&
                    p.PlaceholderKey.ToLower() == req.PlaceholderKey.ToLower()
                );

            if (entity == null)
                return NotFound(new { Message = "Placeholder not found" });

            _dbContext.PlaceholderDefinitions.Remove(entity);
            await _dbContext.SaveChangesAsync();

            return Ok(new
            {
                Success = true,
                Message = "Placeholder definition deleted",
                PlaceholderKey = req.PlaceholderKey
            });
        }





        [HttpPost("rename-Template")]
        public async Task<IActionResult> RenameTemplate([FromBody] RenameTemplate rename)
        {
            var updatedTemplate = await _campaignService.RenameTemplate(rename);

            if (updatedTemplate == null)
                return NotFound("Template not found.");

            return Ok("Template Renamed Successfully");
        }

        [HttpPost("clone-template")]
        public async Task<IActionResult> CloneTemplate([FromQuery] string clientId, [FromQuery] int templateId, [FromQuery] string Name)
        {
            var clonedTemplate = await _campaignService.CloneTemplateAsync(clientId, templateId, Name);

            if (clonedTemplate == null)
                return NotFound("Original template not found.");

            return Ok("Template clone Successfully");
        }

        // ==========================
        // 📸 IMAGE UPLOAD (CHAT)
        // ==========================
        [HttpPost("images/upload-image")]
        public async Task<IActionResult> UploadImage(
            [FromForm] IFormFile image,
            [FromForm] string userId,
            [FromServices] IWebHostEnvironment env
        )
        {
            if (image == null || image.Length == 0)
                return BadRequest("No file uploaded");

            if (string.IsNullOrWhiteSpace(userId))
                return BadRequest("UserId is required");

            // ✅ CORRECT ROOT PATH
            var uploadsPath = Path.Combine(
                env.WebRootPath,   // <-- THIS FIXES PROD
                "uploads",
                userId
            );

            Directory.CreateDirectory(uploadsPath);

            var fileName = $"{Guid.NewGuid()}{Path.GetExtension(image.FileName)}";
            var fullPath = Path.Combine(uploadsPath, fileName);

            await using (var stream = new FileStream(fullPath, FileMode.Create))
            {
                await image.CopyToAsync(stream);
            }

            // ⚠️ DO NOT USE Request.Scheme/Host IN PROD
            var imageUrl = $"https://app.pitchkraft.ai/uploads/{userId}/{fileName}";

            return Ok(new ImageUploadResponse
            {
                ImageUrl = imageUrl
            });
        }

        [HttpPost("campaign/generate-single-contact")]
        public async Task<IActionResult> GenerateSingleContactCampaignEmail(
        [FromBody] GenerateSingleContactCampaignEmailRequest request)
        {
            try
            {
                if (request == null)
                    return BadRequest(new { Message = "Request body is required" });

                if (request.BlueprintId <= 0)
                    return BadRequest(new { Message = "Valid BlueprintId is required" });

                if (request.ContactId <= 0)
                    return BadRequest(new { Message = "Valid ContactId is required" });

                if (string.IsNullOrWhiteSpace(request.ClientId))
                    return BadRequest(new { Message = "ClientId is required" });

                if (!int.TryParse(request.ClientId, out var parsedClientId))
                    return BadRequest(new { Message = "ClientId must be numeric" });


                var template = await _dbContext.CampaignTemplates
                    .Include(t => t.TemplateDefinition)
                    .FirstOrDefaultAsync(t =>
                        t.Id == request.BlueprintId &&
                        t.ClientId == request.ClientId);

                if (template == null)
                    return NotFound(new { Message = "Campaign template not found" });

                if (template.TemplateDefinition == null)
                    return StatusCode(500, new { Message = "Template definition is missing" });

                var contact = await _dbContext.contacts
                    .Include(c => c.data_file)
                    .FirstOrDefaultAsync(c =>
                        c.id == request.ContactId &&
                        c.data_file.client_id == parsedClientId);

                if (contact == null)
                    return NotFound(new { Message = "Contact not found" });

                if (!request.OverwriteExisting && !string.IsNullOrWhiteSpace(contact.email_body))
                {
                    return Ok(new
                    {
                        Success = true,
                        Message = "Email already exists for this contact",
                        Generated = false,
                        ContactId = contact.id,
                        EmailSubject = contact.email_subject,
                        EmailBody = contact.email_body
                    });
                }

                var campaignPlaceholderValues =
                    string.IsNullOrWhiteSpace(template.PlaceholderValues)
                        ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        : JsonSerializer.Deserialize<Dictionary<string, string>>(template.PlaceholderValues)
                          ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                var customFields = await (
                    from value in _dbContext.contact_custom_field_values
                    join field in _dbContext.crm_custom_fields
                        on value.field_id equals field.id
                    where value.contact_id == request.ContactId
                    select new
                    {
                        field.field_name,
                        value.value
                    }
                ).ToDictionaryAsync(
                    x => x.field_name,
                    x => x.value ?? "",
                    StringComparer.OrdinalIgnoreCase
                );

                var currentDate = DateTime.UtcNow.ToString("MMMM d, yyyy");


                var generationNotes = await GetGenerationNotesAsync(parsedClientId, request.ContactId);

                var runtimeReplacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["full_name"] = contact.full_name ?? $"{contact.first_name} {contact.last_name}".Trim(),
                    ["first_name"] = contact.first_name ?? "",
                    ["last_name"] = contact.last_name ?? "",
                    ["company_name"] = contact.company_name ?? "",
                    ["company_name_friendly"] = contact.company_name ?? "",
                    ["job_title"] = contact.job_title ?? "",
                    ["location"] = contact.country_or_address ?? "",
                    ["linkedin_url"] = contact.linkedin_url ?? "",
                    ["website"] = contact.website ?? "",
                    ["linkedin_info"] = StripHtml(contact.linkedIninformation),
                    ["date"] = currentDate,
                    ["notes"] = generationNotes,
                    ["search_output_summary"] = ""
                };



                foreach (var kv in customFields)
                    runtimeReplacements[kv.Key] = kv.Value ?? "";

                var campaignBlueprint = ApplyPlaceholders(
                    template.TemplateDefinition.MasterBlueprintUnpopulated ?? "",
                    campaignPlaceholderValues
                );

                var finalPrompt = ApplyPlaceholders(
                    campaignBlueprint,
                    runtimeReplacements
                );

                var systemPrompt = ApplyPlaceholders(
                    template.TemplateDefinition.AIInstructions ?? "",
                    runtimeReplacements
                );

                var selectedModel = !string.IsNullOrWhiteSpace(template.SelectedModel)
                    ? template.SelectedModel
                    : (!string.IsNullOrWhiteSpace(template.TemplateDefinition.SelectedModel)
                        ? template.TemplateDefinition.SelectedModel
                        : "gpt-5.1");

                var bodyResult = await GeneratePitchByProviderAsync(new EnquiryRequest
                {
                    Prompt = finalPrompt,
                    ScrappedData = systemPrompt,
                    ModelName = selectedModel
                });

                if (!bodyResult.IsSuccess || string.IsNullOrWhiteSpace(bodyResult.Content))
                {
                    return StatusCode(500, new
                    {
                        Message = "Failed to generate email body",
                        Error = bodyResult.Content
                    });
                }

                string subjectLine = "";
                PitchResult? subjectResult = null;

                var aiMode = campaignPlaceholderValues.TryGetValue("email_subject-AI", out var aiModeValue)
                    ? (aiModeValue ?? "").Trim().ToLower()
                    : "yes";

                var manualSubjectTemplate = campaignPlaceholderValues.TryGetValue("email_subject-manual", out var manualVal)
                    ? manualVal ?? ""
                    : "";

                var subjectReplacements = new Dictionary<string, string>(runtimeReplacements, StringComparer.OrdinalIgnoreCase)
                {
                    ["generated_pitch"] = bodyResult.Content
                };

                if (aiMode == "no" && !string.IsNullOrWhiteSpace(manualSubjectTemplate))
                {
                    subjectLine = ApplyPlaceholders(manualSubjectTemplate, subjectReplacements);
                }
                else if (!string.IsNullOrWhiteSpace(template.SubjectInstructions))
                {
                    var filledSubjectInstruction = ApplyPlaceholders(
                        template.SubjectInstructions,
                        subjectReplacements
                    );

                    subjectResult = await GeneratePitchByProviderAsync(new EnquiryRequest
                    {
                        Prompt = bodyResult.Content,
                        ScrappedData = filledSubjectInstruction,
                        ModelName = selectedModel
                    });

                    if (subjectResult.IsSuccess)
                        subjectLine = subjectResult.Content ?? "";
                }

                contact.email_body = bodyResult.Content;
                contact.email_subject = subjectLine;
                contact.updated_at = DateTime.UtcNow;

                await _dbContext.SaveChangesAsync();

                return Ok(new
                {
                    Success = true,
                    Generated = true,
                    BlueprintId = request.BlueprintId,
                    ContactId = request.ContactId,
                    ClientId = request.ClientId,
                    EmailSubject = subjectLine,
                    EmailBody = bodyResult.Content,
                    Usage = new
                    {
                        BodyTokens = bodyResult.TotalTokens,
                        BodyCost = bodyResult.CurrentCost,
                        SubjectTokens = subjectResult?.TotalTokens ?? 0,
                        SubjectCost = subjectResult?.CurrentCost ?? 0,
                        TotalTokens = bodyResult.TotalTokens + (subjectResult?.TotalTokens ?? 0),
                        TotalCost = bodyResult.CurrentCost + (subjectResult?.CurrentCost ?? 0)
                    }
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    Message = "Error generating single-contact email",
                    Error = ex.Message
                });
            }
        }
        private static string StripHtml(string? input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return "";

            return Regex.Replace(input, "<.*?>", "").Trim();
        }

        private async Task<string> GetGenerationNotesAsync(int clientId, int contactId)
        {
            try
            {
                var result = await _noteRepository.GetAllNote(clientId, contactId);

                if (result == null)
                    return "";

                var rawJson = JsonSerializer.Serialize(result);
                using var doc = JsonDocument.Parse(rawJson);

                var root = doc.RootElement;

                if (!root.TryGetProperty("success", out var successProp) || !successProp.GetBoolean())
                    return "";

                if (!root.TryGetProperty("data", out var dataProp) || dataProp.ValueKind != JsonValueKind.Array)
                    return "";

                var usableNotes = new List<string>();

                foreach (var item in dataProp.EnumerateArray())
                {
                    var useInGeneration = item.TryGetProperty("isUseInGenration", out var useProp)
                        && useProp.ValueKind == JsonValueKind.True;

                    if (!useInGeneration)
                        continue;

                    var note = item.TryGetProperty("note", out var noteProp)
                        ? noteProp.GetString() ?? ""
                        : "";

                    if (string.IsNullOrWhiteSpace(note))
                        continue;

                    var cleaned = WebUtility.HtmlDecode(note);
                    cleaned = Regex.Replace(cleaned, "<[^>]*>", " ");
                    cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();

                    if (!string.IsNullOrWhiteSpace(cleaned))
                        usableNotes.Add(cleaned);
                }

                return string.Join("\n", usableNotes);
            }
            catch
            {
                return "";
            }
        }

        private static bool IsDeepSeekModel(string? modelName)
        {
            return modelName?.StartsWith("deepseek-", StringComparison.OrdinalIgnoreCase) == true;
        }

        private Task<PitchResult> GeneratePitchByProviderAsync(EnquiryRequest request)
        {
            return IsDeepSeekModel(request.ModelName)
                ? _deepSeekService.GeneratePitchAsync(request)
                : _pitchService.GeneratePitchAsync(request);
        }



        [HttpPost("template/update-model")]
        public async Task<IActionResult> UpdateCampaignTemplateModel(
            [FromBody] UpdateCampaignTemplateModelRequest request)
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            var template = await _dbContext.CampaignTemplates
                .FirstOrDefaultAsync(t => t.Id == request.TemplateId);

            if (template == null)
                return NotFound(new { Message = "Campaign template not found" });

            template.SelectedModel = request.SelectedModel.Trim();
            template.UpdatedAt = DateTime.UtcNow;

            await _dbContext.SaveChangesAsync();

            return Ok(new
            {
                Success = true,
                Message = "Campaign template model updated successfully",
                template.Id,
                template.SelectedModel
            });
        }

        // Resolve the AI model from the base TemplateDefinition (always GPT).
        // CampaignTemplates.SelectedModel is deepseek-v4-flash and must NOT be sent to OpenAI.


    }


}
