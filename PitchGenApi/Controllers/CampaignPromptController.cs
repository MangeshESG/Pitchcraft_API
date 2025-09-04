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

        // Start with system prompt
        [HttpPost("start")]
        public async Task<IActionResult> StartCampaign([FromBody] StartCampaignDto request)
        {
            if (string.IsNullOrWhiteSpace(request.UserId) || string.IsNullOrWhiteSpace(request.SystemPrompt))
                return BadRequest(new { Message = "UserId and SystemPrompt are required" });

            var result = await _campaignService.StartCampaignAsync(request.UserId, request.SystemPrompt);
            return Ok(new { Response = result }); // align with Chat endpoint
        }

        // Continue chat
        [HttpPost("chat")]
        public async Task<IActionResult> CampaignChat([FromBody] CampaignChatDto request)
        {
            if (string.IsNullOrWhiteSpace(request.UserId) || string.IsNullOrWhiteSpace(request.Message))
                return BadRequest(new { Message = "UserId and Message are required" });

            var result = await _campaignService.CampaignChatAsync(request.UserId, request.Message);
            return Ok(new { Response = result });
        }

     
    }
}