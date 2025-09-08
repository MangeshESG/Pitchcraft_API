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
        private readonly WebSearchService _webSearchService; // ✅ Add this


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

            // ✅ Default to GPT-5 (tool-enabled)
            var model = string.IsNullOrWhiteSpace(request.Model) ? "gpt-5" : request.Model;
            var result = await _campaignService.StartCampaignAsync(request.UserId, request.SystemPrompt, model);
            return Ok(new { Response = result });
        }

        [HttpPost("chat")]
        public async Task<IActionResult> CampaignChat([FromBody] CampaignChatDto request)
        {
            if (string.IsNullOrWhiteSpace(request.UserId) || string.IsNullOrWhiteSpace(request.Message))
                return BadRequest(new { Message = "UserId and Message are required" });

            var model = string.IsNullOrWhiteSpace(request.Model) ? "gpt-5" : request.Model;
            var result = await _campaignService.CampaignChatAsync(request.UserId, request.Message, model);

            return Ok(new { Response = result });
        }


        //[HttpGet("web-search")]
        //public async Task<IActionResult> WebSearch([FromQuery] string query)
        //{
        //    if (string.IsNullOrWhiteSpace(query))
        //        return BadRequest(new { Message = "Query parameter is required" });

        //    var result = await _webSearchService.SearchAsync(query);
        //    return Ok(new { Response = result });
        //}


    }
}