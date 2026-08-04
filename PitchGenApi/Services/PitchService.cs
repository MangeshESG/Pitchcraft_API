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
        private readonly ContactRepository _contactRepository;

        public PitchService(
            HttpClient httpClient,
            AppDbContext context,
            IOptions<OpenAISettings> openAIOptions, ContactRepository contactRepository)
        {
            _httpClient = httpClient;
            _context = context;
            _apiKey = openAIOptions.Value.ApiKey;
            _contactRepository = contactRepository;
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
                AiModelDefaults.IsSearchPreviewModel(request.ModelName);

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

                // A 200 can still be a truncated generation, and reasoning models burn the
                // output budget before writing any text. Report the spend, but not as success.
                string? incompleteReason = OpenAiResponseGuard.GetIncompleteReason(parsed);

                if (incompleteReason != null || string.IsNullOrWhiteSpace(output))
                {
                    return new PitchResult
                    {
                        Content = OpenAiResponseGuard.DescribeEmptyOutput(incompleteReason, rate.MaxTokens),
                        PromptTokens = promptTokens,
                        CompletionTokens = completionTokens,
                        TotalTokens = totalTokens,
                        CurrentCost = currentCost,
                        IsSuccess = false
                    };
                }

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

        public async Task<PitchResult> GenerateWebSearchAsync(EnquiryRequest request, int clientid)
        {
            if (string.IsNullOrWhiteSpace(request.Prompt))
                return new PitchResult
                {
                    Content = "Prompt is required.",
                    IsSuccess = false
                };

            if (string.IsNullOrWhiteSpace(request.ModelName))
                request.ModelName = AiModelDefaults.WebSearchModel;

            var rate = await _context.ModelRates
                .FirstOrDefaultAsync(m => m.ModelName == request.ModelName);

            if (rate == null)
            {
                return new PitchResult
                {
                    Content = $"Model pricing not found for '{request.ModelName}'.",
                    IsSuccess = false
                };
            }

            // Web search reaches OpenAI two different ways depending on the model,
            // so build the request for whichever family we were handed.
            bool isSearchPreviewModel =
                AiModelDefaults.IsSearchPreviewModel(request.ModelName);

            string endpoint;
            object requestData;

            if (isSearchPreviewModel)
            {
                // Chat Completions — these models search natively.
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

                endpoint = "https://api.openai.com/v1/chat/completions";
                requestData = new
                {
                    model = request.ModelName,
                    messages = messages,
                    max_tokens = rate.MaxTokens,
                    web_search_options = new { }
                };
            }
            else
            {
                // Responses API — search has to be attached as an explicit tool.
                var input = new List<object>();

                if (!string.IsNullOrWhiteSpace(request.ScrappedData))
                {
                    input.Add(new
                    {
                        role = "system",
                        content = request.ScrappedData
                    });
                }

                input.Add(new
                {
                    role = "user",
                    content = request.Prompt
                });

                endpoint = "https://api.openai.com/v1/responses";
                requestData = new
                {
                    model = request.ModelName,
                    input = input,
                    max_output_tokens = rate.MaxTokens,
                    tools = new object[]
                    {
                        new { type = "web_search_preview" }
                    }
                };
            }

            var requestBody =
                JsonConvert.SerializeObject(requestData);

            var content =
                new StringContent(
                    requestBody,
                    Encoding.UTF8,
                    "application/json");

            try
            {
                var response = await _httpClient.PostAsync(endpoint, content);

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

                string output;
                int promptTokens;
                int completionTokens;
                decimal webSearchCost = 0m;

                if (isSearchPreviewModel)
                {
                    output =
                        parsed["choices"]?[0]?["message"]?["content"]?.ToString()
                        ?? "";

                    promptTokens =
                        parsed["usage"]?["prompt_tokens"]?.Value<int>() ?? 0;

                    completionTokens =
                        parsed["usage"]?["completion_tokens"]?.Value<int>() ?? 0;
                }
                else
                {
                    output = parsed["output_text"]?.ToString() ?? "";
                    if (string.IsNullOrWhiteSpace(output))
                        output = ExtractText(parsed);

                    promptTokens =
                        parsed["usage"]?["input_tokens"]?.Value<int>() ?? 0;

                    completionTokens =
                        parsed["usage"]?["output_tokens"]?.Value<int>() ?? 0;

                    // Tool-based search is billed per call on top of tokens,
                    // matching the accounting in GeneratePitchAsync.
                    bool usedWebSearch =
                        parsed["output"]?
                            .Any(o => o["type"]?.ToString() == "web_search_call") ?? false;

                    webSearchCost = usedWebSearch ? 0.025m : 0m;
                }

                int totalTokens =
                    parsed["usage"]?["total_tokens"]?.Value<int>()
                    ?? (promptTokens + completionTokens);

                decimal inputCost =
                    promptTokens * rate.InputPrice / 1_000_000m;

                decimal outputCost =
                    completionTokens * rate.OutputPrice / 1_000_000m;

                decimal currentCost =
                    inputCost + outputCost + webSearchCost;

                await _contactRepository.CreditDeduction(clientid);

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
