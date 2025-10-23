using Microsoft.AspNetCore.Mvc;
using PitchGenApi.Services;
using PitchGenApi.Model.DTOs;
using PitchGenApi.Model;
using System.Threading.Tasks;
using PitchGenApi.Models;
using Microsoft.EntityFrameworkCore;
using PitchGenApi.Database;
using System.Text.Json;

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
                    IsActive = true
                };

                _dbContext.CampaignTemplateDefinitions.Add(templateDef);
                await _dbContext.SaveChangesAsync();

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

                await _dbContext.SaveChangesAsync();

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

                // Create campaign template
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
                    CreatedAt = DateTime.UtcNow
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
                        TemplateName = t.TemplateDefinition != null ? t.TemplateDefinition.TemplateName : "",
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
        [HttpGet("campaign/{templateId}")] // ✅ Changed route to avoid conflict
        public async Task<IActionResult> GetCampaignTemplateDetails(int templateId)
        {
            try
            {
                var template = await _dbContext.CampaignTemplates
                    .Include(t => t.TemplateDefinition)
                    .Include(t => t.Conversation)
                    .FirstOrDefaultAsync(t => t.Id == templateId);

                if (template == null)
                    return NotFound(new { Message = "Template not found" });

                if (template.TemplateDefinition == null)
                    return StatusCode(500, new { Message = "Template definition is missing" });

                // Parse placeholder values
                Dictionary<string, string>? placeholderValues = null;
                if (!string.IsNullOrEmpty(template.PlaceholderValues))
                {
                    placeholderValues = JsonSerializer.Deserialize<Dictionary<string, string>>(template.PlaceholderValues);
                }

                // Parse conversation messages
                List<ConversationMessage>? messages = null;
                if (template.Conversation?.ConversationData != null)
                {
                    messages = JsonSerializer.Deserialize<List<ConversationMessage>>(template.Conversation.ConversationData);
                }

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
                    CampaignBlueprint = template.CampaignBlueprint,
                    PlaceholderValues = placeholderValues,
                    SelectedModel = template.SelectedModel,
                    CreatedAt = template.CreatedAt,
                    UpdatedAt = template.UpdatedAt,
                    Conversation = template.Conversation == null ? null : new ConversationData
                    {
                        Messages = messages ?? new List<ConversationMessage>(),
                        StartedAt = template.Conversation.StartedAt,
                        CompletedAt = template.Conversation.CompletedAt
                    }
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

                if (!string.IsNullOrEmpty(request.CampaignBlueprint))
                    template.CampaignBlueprint = request.CampaignBlueprint;

                if (request.PlaceholderValues != null)
                    template.PlaceholderValues = JsonSerializer.Serialize(request.PlaceholderValues);

                if (!string.IsNullOrEmpty(request.SelectedModel))
                    template.SelectedModel = request.SelectedModel;

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

        #endregion

        // ========================================
        // ✅ ADD THIS NEW REGION FOR EDIT MODE
        // ========================================

        #region Edit Mode Endpoints

        // Start edit conversation with historical context
        [HttpPost("edit/start")]
        public async Task<IActionResult> StartEditConversation([FromBody] StartEditConversationRequest request)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request.UserId))
                    return BadRequest(new { Message = "UserId is required" });

                if (request.CampaignTemplateId <= 0)
                    return BadRequest(new { Message = "Valid CampaignTemplateId is required" });

                if (string.IsNullOrWhiteSpace(request.Placeholder))
                    return BadRequest(new { Message = "Placeholder is required" });

                // Get the campaign template with conversation history
                var template = await _dbContext.CampaignTemplates
                    .Include(t => t.TemplateDefinition)
                    .Include(t => t.Conversation)
                    .FirstOrDefaultAsync(t => t.Id == request.CampaignTemplateId);

                if (template == null)
                    return NotFound(new { Message = "Template not found" });

                if (template.TemplateDefinition == null)
                    return StatusCode(500, new { Message = "Template definition is missing" });

                // Get placeholder values
                Dictionary<string, string>? placeholderValues = null;
                if (!string.IsNullOrEmpty(template.PlaceholderValues))
                {
                    placeholderValues = JsonSerializer.Deserialize<Dictionary<string, string>>(template.PlaceholderValues);
                }

                // Get old conversation messages
                List<ConversationMessage>? oldMessages = null;
                if (template.Conversation?.ConversationData != null)
                {
                    oldMessages = JsonSerializer.Deserialize<List<ConversationMessage>>(template.Conversation.ConversationData);
                }

                // Get AI instructions for editing
                string editInstructions = template.TemplateDefinition.AIInstructionsForEdit;
                if (string.IsNullOrEmpty(editInstructions))
                {
                    // Fallback to default edit instructions
                    editInstructions = GetDefaultEditInstructions();
                }

                // Replace placeholders in edit instructions
                editInstructions = editInstructions
                    .Replace("{placeholder}", request.Placeholder)
                    .Replace("{currentValue}", request.CurrentValue);

                // Start edit conversation with context
                var result = await _campaignService.StartEditConversationAsync(
                    request.UserId,
                    request.CampaignTemplateId,
                    request.Placeholder,
                    request.CurrentValue,
                    editInstructions,
                    oldMessages,
                    placeholderValues,
                    request.Model
                );

                return Ok(new { Response = result });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Error = ex.Message });
            }
        }

        // Continue edit conversation
        [HttpPost("edit/chat")]
        public async Task<IActionResult> EditChat([FromBody] EditChatRequest request)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request.UserId))
                    return BadRequest(new { Message = "UserId is required" });

                if (request.CampaignTemplateId <= 0)
                    return BadRequest(new { Message = "Valid CampaignTemplateId is required" });

                if (string.IsNullOrWhiteSpace(request.Message))
                    return BadRequest(new { Message = "Message is required" });

                var result = await _campaignService.ContinueEditConversationAsync(
                    request.UserId,
                    request.CampaignTemplateId,
                    request.Message,
                    request.Model
                );

                return Ok(new { Response = result });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Error = ex.Message });
            }
        }

        // Clear edit session
        [HttpPost("edit/clear/{userId}/{campaignTemplateId}")]
        public IActionResult ClearEditSession(string userId, int campaignTemplateId)
        {
            try
            {
                _campaignService.ClearEditSession(userId, campaignTemplateId);
                return Ok(new { Message = "Edit session cleared successfully" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Error = ex.Message });
            }
        }

        // Helper method for default edit instructions
        private string GetDefaultEditInstructions()
        {
            return @"You are an AI assistant helping to edit a specific placeholder value in a campaign template. 
The user wants to modify the value for {placeholder}.
Current value: ""{currentValue}""

CONTEXT FROM ORIGINAL CONVERSATION:
You previously helped create this campaign. Review the original conversation to understand the context and maintain consistency.

Your task:
1. Review the original conversation context to understand how this placeholder was originally filled
2. Ask the user what new value they want for {placeholder}
3. Ensure the new value maintains consistency with other placeholders and the campaign's tone
4. Confirm the new value with them
5. When confirmed, return the response in this EXACT format:

==PLACEHOLDER_UPDATE_START==
{placeholder} = [new value here]
==PLACEHOLDER_UPDATE_END==

{
  ""status"": ""complete"",
  ""updated_placeholder"": ""{placeholder}"",
  ""old_value"": ""{currentValue}"",
  ""new_value"": ""[new value here]""
}";
        }

        #endregion
    }
}