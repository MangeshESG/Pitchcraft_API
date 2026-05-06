using System.Text;
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

            _httpClient.DefaultRequestHeaders.Clear();

            _httpClient.DefaultRequestHeaders.Add(
                "Authorization",
                $"Bearer {_apiKey}");
        }

        public async Task<PitchResult> GeneratePitchAsync(
            EnquiryRequest request)
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

                var requestBody = new
                {
                    model = request.ModelName,

                    messages = new object[]
                    {
                        new
                        {
                            role = "user",
                            content = request.Prompt
                        }
                    },

                    temperature = 0.7,
                    max_tokens = 2000,
                    stream = false
                };

                var json =
                    JsonConvert.SerializeObject(requestBody);

                var response =
                    await _httpClient.PostAsync(
                        "https://api.deepseek.com/chat/completions",
                        new StringContent(
                            json,
                            Encoding.UTF8,
                            "application/json"));

                var responseContent =
                    await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    return new PitchResult
                    {
                        Content = responseContent,
                        IsSuccess = false
                    };
                }

                var parsed = JObject.Parse(responseContent);

                string output =
                    parsed["choices"]?[0]?["message"]?["content"]
                    ?.ToString() ?? "";

                int promptTokens =
                    parsed["usage"]?["prompt_tokens"]
                    ?.Value<int>() ?? 0;

                int completionTokens =
                    parsed["usage"]?["completion_tokens"]
                    ?.Value<int>() ?? 0;

                int totalTokens =
                    parsed["usage"]?["total_tokens"]
                    ?.Value<int>() ?? 0;

                // DeepSeek Pricing
                decimal inputPricePerMillion = 0.27m;

                decimal outputPricePerMillion = 1.10m;

                decimal currentCost =
                    (promptTokens * inputPricePerMillion / 1_000_000m)
                    +
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
            catch (Exception ex)
            {
                return new PitchResult
                {
                    Content = ex.Message,
                    IsSuccess = false
                };
            }
        }
    }
}