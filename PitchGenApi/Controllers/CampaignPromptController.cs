using Microsoft.AspNetCore.Mvc;
using PitchGenApi.Services;
using PitchGenApi.Model.DTOs;
using PitchGenApi.Model;
using System.Threading.Tasks;
using PitchGenApi.Models;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PitchGenApi.Database;
using System;
using System.Linq;
using System.Collections.Generic;

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

        // Existing chat endpoint
        [HttpPost("chat")]
        public async Task<IActionResult> Chat([FromBody] ChatRequestDto request)
        {
            if (string.IsNullOrWhiteSpace(request.UserId))
                return BadRequest(new { Message = "UserId is required" });

            var model = string.IsNullOrWhiteSpace(request.Model) ? "gpt-5" : request.Model;

            var result = await _campaignService.ProcessChatAsync(
                request.UserId,
                request.Message,
                request.SystemPrompt,
                model
            );

            return Ok(new { Response = result });
        }

        // Save campaign template with conversation
        [HttpPost("template/save")]
        public async Task<IActionResult> SaveCampaignTemplate([FromBody] SaveCampaignTemplateRequest request)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request.ClientId))
                    return BadRequest(new { Message = "ClientId is required" });

                if (string.IsNullOrWhiteSpace(request.TemplateName))
                    return BadRequest(new { Message = "Template name is required" });

                // Create campaign template
                var campaignTemplate = new CampaignTemplate
                {
                    ClientId = request.ClientId,
                    TemplateName = request.TemplateName,
                    AIInstructions = request.SystemPrompt, // Changed
                    PlaceholderListInfo = request.MasterPrompt, // Changed
                    MasterBlueprintUnpopulated = request.PreviewText, // Changed
                    PlaceholderListWithValue = request.FinalPrompt, // Changed
                    CampaignBlueprint = request.FinalPreviewText, // Changed
                    PlaceholderValues = JsonSerializer.Serialize(request.PlaceholderValues),
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

        // Get saved templates for a client
        [HttpGet("templates/{clientId}")]
        public async Task<IActionResult> GetCampaignTemplates(string clientId, [FromQuery] int pageSize = 20, [FromQuery] int pageNumber = 1)
        {
            try
            {
                var query = _dbContext.CampaignTemplates
                    .Where(t => t.ClientId == clientId)
                    .OrderByDescending(t => t.CreatedAt);

                var totalCount = await query.CountAsync();

                var templates = await query
                    .Skip((pageNumber - 1) * pageSize)
                    .Take(pageSize)
                    .Select(t => new
                    {
                        t.Id,
                        t.TemplateName,
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

        // Get specific template with full details
        [HttpGet("template/{templateId}")]
        public async Task<IActionResult> GetCampaignTemplate(int templateId)
        {
            try
            {
                var template = await _dbContext.CampaignTemplates
                    .Include(t => t.Conversation)
                    .FirstOrDefaultAsync(t => t.Id == templateId);

                if (template == null)
                    return NotFound(new { Message = "Template not found" });

                // Parse placeholder values
                Dictionary<string, string> placeholderValues = null;
                if (!string.IsNullOrEmpty(template.PlaceholderValues))
                {
                    placeholderValues = JsonSerializer.Deserialize<Dictionary<string, string>>(template.PlaceholderValues);
                }

                var result = new
                {
                    template.Id,
                    template.ClientId,
                    template.TemplateName,
                    template.AIInstructions, // Changed from SystemPrompt
                    template.PlaceholderListInfo, // Changed from MasterPrompt
                    template.MasterBlueprintUnpopulated, // Changed from PreviewText
                    template.PlaceholderListWithValue, // Changed from FinalPrompt
                    template.CampaignBlueprint, // Changed from FinalPreviewText
                    PlaceholderValues = placeholderValues,
                    template.SelectedModel,
                    template.CreatedAt,
                    template.UpdatedAt,
                    Conversation = template.Conversation == null ? null : new
                    {
                        Messages = JsonSerializer.Deserialize<List<ConversationMessage>>(template.Conversation.ConversationData),
                        template.Conversation.StartedAt,
                        template.Conversation.CompletedAt
                    }
                };


                return Ok(result);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Error = ex.Message });
            }
        }

        // Update campaign template
        [HttpPost("template/update")]
        public async Task<IActionResult> UpdateCampaignTemplate([FromBody] UpdateCampaignTemplateRequest request)
        {
            try
            {
                var template = await _dbContext.CampaignTemplates
                    .FirstOrDefaultAsync(t => t.Id == request.Id);

                if (template == null)
                    return NotFound(new { Message = "Template not found" });

                // Update fields with new property names
                template.TemplateName = request.TemplateName;
                template.AIInstructions = request.AIInstructions;
                template.PlaceholderListInfo = request.PlaceholderListInfo;
                template.MasterBlueprintUnpopulated = request.MasterBlueprintUnpopulated;

                // Only update these if provided
                if (!string.IsNullOrEmpty(request.PlaceholderListWithValue))
                    template.PlaceholderListWithValue = request.PlaceholderListWithValue;

                if (!string.IsNullOrEmpty(request.CampaignBlueprint))
                    template.CampaignBlueprint = request.CampaignBlueprint;

                template.SelectedModel = request.SelectedModel;

                // Update placeholder values if provided
                if (request.PlaceholderValues != null)
                {
                    template.PlaceholderValues = JsonSerializer.Serialize(request.PlaceholderValues);
                }

                template.UpdatedAt = DateTime.UtcNow;

                await _dbContext.SaveChangesAsync();

                return Ok(new { Success = true, Message = "Template updated successfully" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Error = ex.Message });
            }
        }

        // Delete template - Changed from DELETE to POST
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

        // Existing endpoints
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
    }
}