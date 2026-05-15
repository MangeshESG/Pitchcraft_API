using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PitchGenApi.Database;
using PitchGenApi.Model;

namespace PitchGenApi.Services
{
    public class DeepSeekPitchService
    {
        private readonly HttpClient _httpClient;
        private readonly AppDbContext _context;
        private readonly string _apiKey;

        public DeepSeekPitchService(
            HttpClient httpClient,
            AppDbContext context,
            IOptions<DeepSeekSettings> options)
        {
            _httpClient = httpClient;
            _context = context;
            _apiKey = options.Value.ApiKey;

            _httpClient.Timeout = TimeSpan.FromMinutes(3);

            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");
            _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
        }

        public async Task<PitchResult> GeneratePitchAsync(EnquiryRequest request)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request.Prompt))
                {
                    return new PitchResult
                    {
                        Content = "Prompt is required.",
                        IsSuccess = false
                    };
                }

                if (string.IsNullOrWhiteSpace(request.ModelName))
                {
                    return new PitchResult
                    {
                        Content = "Model name is required.",
                        IsSuccess = false
                    };
                }

                string requestedModelName = request.ModelName.Trim();

                bool thinkingEnabled = requestedModelName.EndsWith(
                    "-thinking",
                    StringComparison.OrdinalIgnoreCase
                );

                string apiModelName = thinkingEnabled
                    ? requestedModelName.Replace("-thinking", "", StringComparison.OrdinalIgnoreCase)
                    : requestedModelName;

                var rate =
                    await _context.ModelRates.FirstOrDefaultAsync(m => m.ModelName == requestedModelName)
                    ?? await _context.ModelRates.FirstOrDefaultAsync(m => m.ModelName == apiModelName);

                decimal inputPricePerMillion = rate?.InputPrice ?? 0.27m;
                decimal outputPricePerMillion = rate?.OutputPrice ?? 1.10m;
                double temperature = Convert.ToDouble(rate?.Temperature ?? 0.7m);
                int maxTokens = rate?.MaxTokens ?? 2000;

                var messages = new List<object>();

                if (!string.IsNullOrWhiteSpace(request.ScrappedData))
                {
                    messages.Add(new
                    {
                        role = "system",
                        content = request.ScrappedData
                    });
                }

                messages.Add(new
                {
                    role = "user",
                    content = request.Prompt
                });

                bool isDeepSeekV4 = apiModelName.StartsWith(
                    "deepseek-v4-",
                    StringComparison.OrdinalIgnoreCase
                );

                var requestBody = new Dictionary<string, object>
                {
                    { "model", apiModelName },
                    { "messages", messages },
                    { "max_tokens", maxTokens },
                    { "stream", false }
                };

                if (isDeepSeekV4)
                {
                    requestBody["thinking"] = new
                    {
                        type = thinkingEnabled ? "enabled" : "disabled"
                    };

                    if (thinkingEnabled)
                    {
                        requestBody["reasoning_effort"] = "high";
                    }
                    else
                    {
                        requestBody["temperature"] = temperature;
                    }
                }
                else
                {
                    requestBody["temperature"] = temperature;
                }

                var json = JsonConvert.SerializeObject(requestBody);

                using var httpContent = new StringContent(
                    json,
                    Encoding.UTF8,
                    "application/json"
                );

                var response = await _httpClient.PostAsync(
                    "https://api.deepseek.com/chat/completions",
                    httpContent
                );

                var responseContent = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    return new PitchResult
                    {
                        Content = $"DeepSeek API Error ({(int)response.StatusCode}): {responseContent}",
                        IsSuccess = false
                    };
                }

                var parsed = JObject.Parse(responseContent);

                string output =
                    parsed["choices"]?[0]?["message"]?["content"]?.ToString() ?? "";

                int promptTokens =
                    parsed["usage"]?["prompt_tokens"]?.Value<int>() ?? 0;

                int completionTokens =
                    parsed["usage"]?["completion_tokens"]?.Value<int>() ?? 0;

                int totalTokens =
                    parsed["usage"]?["total_tokens"]?.Value<int>()
                    ?? promptTokens + completionTokens;

                decimal currentCost =
                    (promptTokens * inputPricePerMillion / 1_000_000m) +
                    (completionTokens * outputPricePerMillion / 1_000_000m);

                return new PitchResult
                {
                    Content = output,
                    PromptTokens = promptTokens,
                    CompletionTokens = completionTokens,
                    TotalTokens = totalTokens,
                    CurrentCost = currentCost,
                    IsSuccess = true
                };
            }
            catch (TaskCanceledException ex)
            {
                return new PitchResult
                {
                    Content = $"DeepSeek request timed out after {_httpClient.Timeout.TotalSeconds} seconds: {ex.Message}",
                    IsSuccess = false
                };
            }
            catch (Exception ex)
            {
                return new PitchResult
                {
                    Content = $"DeepSeek request failed: {ex.Message}",
                    IsSuccess = false
                };
            }
        }
    }
}