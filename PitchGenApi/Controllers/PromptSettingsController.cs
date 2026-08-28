using Microsoft.AspNetCore.Mvc;
using PitchGenApi.Interfaces;
using PitchGenApi.Model;
using PitchGenApi.Model.DTOs;

namespace PitchGenApi.Controllers
{
    /// <summary>
    /// Admin-only editor for the AI instructions the API runs (Settings &gt;
    /// Admin &gt; Prompts in the app). Today that is the email research prompt
    /// behind the extension's unlock button. The stored text is the only copy:
    /// clearing a prompt turns the feature behind it off.
    /// </summary>
    [ApiController]
    [Route("api/prompt-settings")]
    public class PromptSettingsController : ControllerBase
    {
        private readonly IPromptSettingsService _promptSettings;

        public PromptSettingsController(IPromptSettingsService promptSettings)
        {
            _promptSettings = promptSettings;
        }

        /// <summary>
        /// Every editable prompt with its stored text and the placeholders it
        /// may use. A prompt nobody has saved comes back empty.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetPrompts()
        {
            try
            {
                var effective = await _promptSettings.GetAllAsync();
                var metadata = await _promptSettings.GetMetadataAsync();

                var prompts = PromptKeys.All.Select(promptKey =>
                    BuildDto(promptKey, effective, metadata)).ToList();

                return Ok(new { Success = true, Prompts = prompts });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Success = false, Message = ex.Message });
            }
        }

        /// <summary>One prompt, for callers that only care about a single key.</summary>
        [HttpGet("{promptKey}")]
        public async Task<IActionResult> GetPrompt(string promptKey)
        {
            try
            {
                if (!PromptKeys.IsKnown(promptKey))
                    return NotFound(new { Success = false, Message = $"Unknown prompt key: {promptKey}" });

                var effective = await _promptSettings.GetAllAsync();
                var metadata = await _promptSettings.GetMetadataAsync();

                return Ok(new
                {
                    Success = true,
                    Prompt = BuildDto(PromptKeys.Normalize(promptKey), effective, metadata)
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Success = false, Message = ex.Message });
            }
        }

        /// <summary>
        /// Saves one or more prompts. A blank text clears that prompt, which
        /// turns the feature behind it off until something is saved again.
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> UpdatePrompts(
            [FromBody] UpdatePromptSettingsRequest request)
        {
            try
            {
                if (request?.Prompts == null || request.Prompts.Count == 0)
                    return BadRequest(new { Success = false, Message = "At least one prompt is required." });

                var unknownKeys = request.Prompts.Keys
                    .Where(key => !PromptKeys.IsKnown(key))
                    .ToList();

                if (unknownKeys.Count > 0)
                {
                    return BadRequest(new
                    {
                        Success = false,
                        Message = $"Unknown prompt key(s): {string.Join(", ", unknownKeys)}"
                    });
                }

                await _promptSettings.SaveAsync(request.Prompts, request.UpdatedBy);

                var effective = await _promptSettings.GetAllAsync();
                var metadata = await _promptSettings.GetMetadataAsync();

                var prompts = PromptKeys.All.Select(promptKey =>
                    BuildDto(promptKey, effective, metadata)).ToList();

                return Ok(new
                {
                    Success = true,
                    Message = "Prompt settings updated.",
                    Prompts = prompts
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Success = false, Message = ex.Message });
            }
        }

        private static PromptSettingDto BuildDto(
            string promptKey,
            IReadOnlyDictionary<string, string> effective,
            IReadOnlyDictionary<string, (DateTime UpdatedAt, string? UpdatedBy)> metadata)
        {
            var (label, description) = PromptKeys.Describe(promptKey);
            var promptText = effective.TryGetValue(promptKey, out var stored) && stored != null
                ? stored
                : "";

            var hasRow = metadata.TryGetValue(promptKey, out var row);

            return new PromptSettingDto
            {
                PromptKey = promptKey,
                Label = label,
                Description = description,
                PromptText = promptText,
                IsConfigured = !string.IsNullOrWhiteSpace(promptText),
                Placeholders = PromptKeys.Placeholders(promptKey).ToList(),
                UpdatedAt = hasRow ? row.UpdatedAt : null,
                UpdatedBy = hasRow ? row.UpdatedBy : null
            };
        }
    }
}
