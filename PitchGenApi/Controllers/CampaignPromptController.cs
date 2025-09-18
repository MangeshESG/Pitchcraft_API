using Microsoft.AspNetCore.Mvc;
using PitchGenApi.Services;
using PitchGenApi.Model.DTOs;
using PitchGenApi.Model;
using System.Threading.Tasks;
using PitchGenApi.Models;

namespace PitchGenApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class CampaignPromptController : ControllerBase
    {
        private readonly CampaignPromptService _campaignService;

        public CampaignPromptController(CampaignPromptService campaignService)
        {
            _campaignService = campaignService;
        }

        // Single endpoint for all chat interactions
        [HttpPost("chat")]
        public async Task<IActionResult> Chat([FromBody] ChatRequestDto request)
        {
            if (string.IsNullOrWhiteSpace(request.UserId))
                return BadRequest(new { Message = "UserId is required" });

            // Default to GPT-5 if not specified
            var model = string.IsNullOrWhiteSpace(request.Model) ? "gpt-5" : request.Model;

            var result = await _campaignService.ProcessChatAsync(
                request.UserId,
                request.Message,
                request.SystemPrompt,
                model
            );

            return Ok(new { Response = result });
        }

        // Optional: Get chat history for a user
        [HttpGet("history/{userId}")]
        public IActionResult GetChatHistory(string userId)
        {
            var history = _campaignService.GetChatHistory(userId);
            if (history == null)
                return NotFound(new { Message = "No chat history found for this user" });

            return Ok(new { History = history });
        }

        // Optional: Clear chat history for a user
        [HttpDelete("history/{userId}")]
        public IActionResult ClearChatHistory(string userId)
        {
            _campaignService.ClearChatHistory(userId);
            return Ok(new { Message = "Chat history cleared" });
        }
    }
}