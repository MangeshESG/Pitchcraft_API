using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PitchGenApi.Database;
using PitchGenApi.Interfaces;
using PitchGenApi.Model;
using PitchGenApi.Model.DTOs;

namespace PitchGenApi.Controllers
{
    /// <summary>
    /// Admin-only control panel for the model behind each AI purpose
    /// (Settings > AI models in the app).
    /// </summary>
    [ApiController]
    [Route("api/ai-model-settings")]
    public class AiModelSettingsController : ControllerBase
    {
        private readonly IAiModelSettingsService _aiModelSettings;
        private readonly AppDbContext _dbContext;

        public AiModelSettingsController(
            IAiModelSettingsService aiModelSettings,
            AppDbContext dbContext)
        {
            _aiModelSettings = aiModelSettings;
            _dbContext = dbContext;
        }

        /// <summary>
        /// Current model per purpose, plus the models that are actually callable
        /// (i.e. the ones with a pricing row) so the admin can't pick a model the
        /// generation pipeline would reject.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetSettings()
        {
            try
            {
                var effective = await _aiModelSettings.GetAllAsync();

                var settings = AiModelPurposes.All.Select(purposeKey =>
                {
                    var (label, description) = AiModelPurposes.Describe(purposeKey);

                    return new AiModelSettingDto
                    {
                        PurposeKey = purposeKey,
                        Label = label,
                        Description = description,
                        ModelName = effective.TryGetValue(purposeKey, out var model)
                            ? model
                            : AiModelDefaults.ForPurpose(purposeKey),
                        DefaultModel = AiModelDefaults.ForPurpose(purposeKey)
                    };
                }).ToList();

                return Ok(new
                {
                    Success = true,
                    Settings = settings,
                    AvailableModels = await GetPricedModelNamesAsync()
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Success = false, Message = ex.Message });
            }
        }

        /// <summary>
        /// Saves one or more purposes. A blank model resets that purpose to its
        /// built-in default.
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> UpdateSettings(
            [FromBody] UpdateAiModelSettingsRequest request)
        {
            try
            {
                if (request?.Settings == null || request.Settings.Count == 0)
                    return BadRequest(new { Success = false, Message = "At least one setting is required." });

                var unknownKeys = request.Settings.Keys
                    .Where(key => !AiModelPurposes.IsKnown(key))
                    .ToList();

                if (unknownKeys.Count > 0)
                {
                    return BadRequest(new
                    {
                        Success = false,
                        Message = $"Unknown purpose key(s): {string.Join(", ", unknownKeys)}"
                    });
                }

                // Only models with a pricing row can be billed, so reject anything
                // else here rather than failing later mid-generation.
                var pricedModels = await GetPricedModelNamesAsync();

                if (pricedModels.Count > 0)
                {
                    var unpriced = request.Settings.Values
                        .Where(model => !string.IsNullOrWhiteSpace(model))
                        .Select(model => model!.Trim())
                        .Where(model => !pricedModels.Contains(model, StringComparer.OrdinalIgnoreCase))
                        .Distinct()
                        .ToList();

                    if (unpriced.Count > 0)
                    {
                        return BadRequest(new
                        {
                            Success = false,
                            Message = $"No pricing configured for model(s): {string.Join(", ", unpriced)}"
                        });
                    }
                }

                var effective = await _aiModelSettings.SaveAsync(request.Settings, request.UpdatedBy);

                return Ok(new
                {
                    Success = true,
                    Message = "AI model settings updated.",
                    Settings = effective
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Success = false, Message = ex.Message });
            }
        }

        private async Task<List<string>> GetPricedModelNamesAsync()
        {
            try
            {
                return await _dbContext.ModelRates
                    .AsNoTracking()
                    .Where(rate => rate.ModelName != null && rate.ModelName != "")
                    .Select(rate => rate.ModelName)
                    .Distinct()
                    .OrderBy(name => name)
                    .ToListAsync();
            }
            catch
            {
                // Without a rates table the UI just falls back to its own list.
                return new List<string>();
            }
        }
    }
}
