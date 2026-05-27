using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PitchGenApi.Database;
using PitchGenApi.Interfaces;
using PitchGenApi.Model;

namespace PitchGenApi.Services
{
    public class PitchService : IPitchService
    {
        private readonly HttpClient _httpClient;
        private readonly AppDbContext _context;
        private readonly string _apiKey;

        public PitchService(
            HttpClient httpClient,
            AppDbContext context,
            IOptions<OpenAISettings> openAIOptions)
        {
            _httpClient = httpClient;
            _context = context;
            _apiKey = openAIOptions.Value.ApiKey;

            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");
            _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
        }

        public async Task<PitchResult> GeneratePitchAsync(EnquiryRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Prompt))
                return new PitchResult { Content = "Prompt is required.", IsSuccess = false };

            if (string.IsNullOrWhiteSpace(request.ModelName))
                return new PitchResult { Content = "Model name is required.", IsSuccess = false };

            // Trim scrapped data
            string systemContent =
                request.ScrappedData.Length > 1000 ? request.ScrappedData[..999] : request.ScrappedData;

            // Get model pricing
            var rate = await _context.ModelRates.FirstOrDefaultAsync(m => m.ModelName == request.ModelName);
            if (rate == null)
            {
                rate = await _context.ModelRates.FirstOrDefaultAsync(m => m.ModelName == "gpt-5.1");
                if (rate == null)
                    return new PitchResult { Content = "Invalid model and fallback model not found.", IsSuccess = false };

                request.ModelName = "gpt-5.1";
            }

            // Build role-tagged input for Responses API
            var sbInput = new StringBuilder();
            sbInput.AppendLine($"system: {systemContent}");
            sbInput.AppendLine($"user: {request.Prompt}");

            bool isSearchPreviewModel =
                request.ModelName.Contains("search-preview");

            var requestData = new Dictionary<string, object>
{
    { "model", request.ModelName },
    { "input", new object[]
        {
            new
            {
                role = "system",
                content = systemContent
            },
            new
            {
                role = "user",
                content = request.Prompt
            }
        }
    },
    { "temperature", rate.Temperature },
    { "max_output_tokens", rate.MaxTokens }
};

            if (!isSearchPreviewModel)
            {
                requestData["tools"] = new object[]
                {
        new { type = "web_search_preview" }
                };
            }

            var requestBody = JsonConvert.SerializeObject(requestData);
            var content = new StringContent(requestBody, Encoding.UTF8, "application/json");

            try
            {
                var response = await _httpClient.PostAsync("https://api.openai.com/v1/responses", content);
                var json = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    return new PitchResult
                    {
                        Content = $"OpenAI API Error: {json}",
                        IsSuccess = false
                    };
                }

                var parsed = JsonConvert.DeserializeObject<JObject>(json)!;

                // Extract assistant response
                string output = parsed["output_text"]?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(output))
                    output = ExtractText(parsed);

                // Extract usage
                int promptTokens = parsed["usage"]?["input_tokens"]?.Value<int>() ?? 0;
                int completionTokens = parsed["usage"]?["output_tokens"]?.Value<int>() ?? 0;
                int totalTokens = parsed["usage"]?["total_tokens"]?.Value<int>()
                                  ?? (promptTokens + completionTokens);

                decimal inputCost =
                    promptTokens * rate.InputPrice / 1_000_000m;

                decimal outputCost =
                    completionTokens * rate.OutputPrice / 1_000_000m;

                bool usedWebSearch =
                    parsed["output"]?
                        .Any(o => o["type"]?.ToString() == "web_search_call") ?? false;

                decimal webSearchCost = 0m;

                if (!isSearchPreviewModel)
                {
                    webSearchCost = usedWebSearch ? 0.025m : 0m;
                }

                decimal currentCost =
                    inputCost +
                    outputCost +
                    webSearchCost;



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
            catch (Exception ex)
            {
                return new PitchResult
                {
                    Content = $"Request failed: {ex.Message}",
                    IsSuccess = false
                };
            }
        }

        public async Task<PitchResult> GenerateWebSearchAsync(EnquiryRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Prompt))
                return new PitchResult
                {
                    Content = "Prompt is required.",
                    IsSuccess = false
                };

            var rate = await _context.ModelRates
                .FirstOrDefaultAsync(m => m.ModelName == request.ModelName);

            if (rate == null)
            {
                return new PitchResult
                {
                    Content = "Model pricing not found.",
                    IsSuccess = false
                };
            }

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

            var requestData = new
            {
                model = request.ModelName,
                messages = messages,
                max_tokens = rate.MaxTokens,
                web_search_options = new { }
            };

            var requestBody =
                JsonConvert.SerializeObject(requestData);

            var content =
                new StringContent(
                    requestBody,
                    Encoding.UTF8,
                    "application/json");

            try
            {
                var response = await _httpClient.PostAsync(
                    "https://api.openai.com/v1/chat/completions",
                    content);

                var json = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    return new PitchResult
                    {
                        Content = $"OpenAI API Error: {json}",
                        IsSuccess = false
                    };
                }

                var parsed = JsonConvert.DeserializeObject<JObject>(json)!;

                string output =
                    parsed["choices"]?[0]?["message"]?["content"]?.ToString()
                    ?? "";

                int promptTokens =
                    parsed["usage"]?["prompt_tokens"]?.Value<int>() ?? 0;

                int completionTokens =
                    parsed["usage"]?["completion_tokens"]?.Value<int>() ?? 0;

                int totalTokens =
                    parsed["usage"]?["total_tokens"]?.Value<int>()
                    ?? (promptTokens + completionTokens);

                decimal inputCost =
                    promptTokens * rate.InputPrice / 1_000_000m;

                decimal outputCost =
                    completionTokens * rate.OutputPrice / 1_000_000m;

                decimal currentCost =
                    inputCost + outputCost;

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
            catch (Exception ex)
            {
                return new PitchResult
                {
                    Content = $"Request failed: {ex.Message}",
                    IsSuccess = false
                };
            }
        }

        private string ExtractText(JObject parsed)
        {
            var outputs = parsed["output"] as JArray;
            if (outputs == null) return "";

            var sb = new StringBuilder();
            foreach (var item in outputs)
            {
                var contentArray = item["content"] as JArray;
                if (contentArray == null) continue;

                foreach (var c in contentArray)
                {
                    string? text = c["text"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(text))
                        sb.AppendLine(text.Trim());
                }
            }

            return sb.ToString().Trim();
        }
    }
}
