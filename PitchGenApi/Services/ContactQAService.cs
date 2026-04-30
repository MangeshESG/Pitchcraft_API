namespace PitchGenApi.Services
{
    using System.Text;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Options;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;
    using PitchGenApi.Database;
    using PitchGenApi.Interfaces;
    using PitchGenApi.Model.DTOs;
    using PitchGenApi.Model;

    public class ContactQAService : IContactQAService
    {
        private readonly HttpClient _httpClient;
        private readonly AppDbContext _context;
        private readonly string _apiKey;

        public ContactQAService(HttpClient httpClient, AppDbContext context, IOptions<OpenAISettings> openAIOptions)
        {
            _httpClient = httpClient;
            _context = context;
            _apiKey = openAIOptions.Value.ApiKey;

            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");
            _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
        }

        public async Task<ContactQAResponse> AskAsync(ContactQARequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Question))
                return new ContactQAResponse { IsSuccess = false, Answer = "Question is required." };

            var rate = await _context.ModelRates.FirstOrDefaultAsync(m => m.ModelName == request.ModelName)
                       ?? await _context.ModelRates.FirstOrDefaultAsync(m => m.ModelName == "gpt-5.1");

            if (rate == null)
                return new ContactQAResponse { IsSuccess = false, Answer = "Model pricing not configured." };

            var systemPrompt = """
You are a contact intelligence assistant.
Answer only from the provided contact context and prior chat.
If the answer is not in the context, say that clearly.
Prefer concise, factual answers.
When asked about prior emails, mention the relevant subject, date, and exact question or intent if present.
""";

            var inputParts = new List<object>
        {
            new
            {
                role = "system",
                content = new object[]
                {
                    new { type = "input_text", text = systemPrompt }
                }
            },
            new
            {
                role = "system",
                content = new object[]
                {
                    new { type = "input_text", text = request.ContextSummary ?? JsonConvert.SerializeObject(request.Context) }
                }
            }
        };

            foreach (var message in request.Messages ?? new List<ContactQAMessageDto>())
            {
                if (string.IsNullOrWhiteSpace(message.Content)) continue;

                var normalizedRole = message.Role?.ToLower() == "assistant" ? "assistant" : "user";
                var contentType = normalizedRole == "assistant" ? "output_text" : "input_text";

                inputParts.Add(new
                {
                    role = normalizedRole,
                    content = new object[]
                    {
            new { type = contentType, text = message.Content }
                    }
                });
            }

            inputParts.Add(new
            {
                role = "user",
                content = new object[]
                {
                new { type = "input_text", text = request.Question }
                }
            });

            var requestData = new Dictionary<string, object>
        {
            { "model", request.ModelName },
            { "input", inputParts },
            { "temperature", rate.Temperature },
            { "max_output_tokens", rate.MaxTokens }
        };

            var requestBody = JsonConvert.SerializeObject(requestData);
            var content = new StringContent(requestBody, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync("https://api.openai.com/v1/responses", content);
            var json = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                return new ContactQAResponse
                {
                    IsSuccess = false,
                    Answer = json
                };
            }

            var parsed = JsonConvert.DeserializeObject<JObject>(json)!;
            var output = parsed["output_text"]?.ToString() ?? "";

            if (string.IsNullOrWhiteSpace(output))
            {
                var outputs = parsed["output"] as JArray;
                if (outputs != null)
                {
                    var sb = new StringBuilder();
                    foreach (var item in outputs)
                    {
                        var contentArray = item["content"] as JArray;
                        if (contentArray == null) continue;

                        foreach (var c in contentArray)
                        {
                            var text = c["text"]?.ToString();
                            if (!string.IsNullOrWhiteSpace(text))
                                sb.AppendLine(text.Trim());
                        }
                    }

                    output = sb.ToString().Trim();
                }
            }

            int promptTokens = parsed["usage"]?["input_tokens"]?.Value<int>() ?? 0;
            int completionTokens = parsed["usage"]?["output_tokens"]?.Value<int>() ?? 0;
            int totalTokens = promptTokens + completionTokens;

            decimal currentCost =
                (promptTokens * rate.InputPrice / 1_000_000m) +
                (completionTokens * rate.OutputPrice / 1_000_000m);

            return new ContactQAResponse
            {
                IsSuccess = true,
                Answer = output,
                PromptTokens = promptTokens,
                CompletionTokens = completionTokens,
                TotalTokens = totalTokens,
                CurrentCost = currentCost
            };
        }
    }

}
