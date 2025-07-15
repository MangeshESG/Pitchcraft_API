using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using PitchGenApi.Database;
using PitchGenApi.Interfaces;
using PitchGenApi.Model;
using static PitchGenApi.Model.ChatGptResponse;

namespace PitchGenApi.Services
{
    public class PitchService : IPitchService
    {
        private readonly HttpClient _httpClient;
        private readonly AppDbContext _context;
        private readonly string _apiKey; // ✅ Declare the field



        public PitchService(HttpClient httpClient, AppDbContext context, IOptions<OpenAISettings> openAIOptions)
        {
            _httpClient = httpClient;
            _context = context;
            _apiKey = openAIOptions.Value.ApiKey;

            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}"); // ✅ Set the header with the key
        }




        public async Task<PitchResult> GeneratePitchAsync(EnquiryRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Prompt))
                return new PitchResult { Content = "Prompt is required.", IsSuccess = false };

            if (string.IsNullOrWhiteSpace(request.ModelName))
                return new PitchResult { Content = "Model name is required.", IsSuccess = false };

            string systemContent = request.ScrappedData.Length > 1000 ? request.ScrappedData.Substring(0, 999) : request.ScrappedData;

            var rate = await _context.ModelRates
                             .FirstOrDefaultAsync(m => m.ModelName == request.ModelName);
            if (rate == null)
            {
                // Default to "gpt-4o-mini" if the model is not found
                rate = await _context.ModelRates.FirstOrDefaultAsync(m => m.ModelName == "gpt-4o-mini");

                if (rate == null)
                {
                    
                    return new PitchResult { Content = "Model not found, and fallback 'gpt-4o-mini' is missing.", IsSuccess = false };
                }

                request.ModelName = "gpt-4o-mini";

            }

            var requestData = new
            {
                //model = "gpt-4o-mini",
                model = request.ModelName,
                messages = new[]
                {
            new { role = "system", content = systemContent },
            new { role = "user", content = request.Prompt }
        },
                temperature = rate.Temperature,
                max_completion_tokens = rate.MaxTokens,
                top_p = 1,
                frequency_penalty = 0,
                presence_penalty = 0
            };

            var requestBody = JsonConvert.SerializeObject(requestData);
            var content = new StringContent(requestBody, Encoding.UTF8, "application/json");

            

            try
            {

                var response = await _httpClient.PostAsync("https://api.openai.com/v1/chat/completions", content);
                var jsonResponse = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    var errorResponse = await response.Content.ReadAsStringAsync();
                    return new PitchResult { Content = $"OpenAI API Error: {errorResponse}", IsSuccess = false };
                }

                var result = JsonConvert.DeserializeObject<ChatCompletionResponse>(jsonResponse);

                if (result?.Choices == null || result.Choices.Length == 0)
                {
                    return new PitchResult { Content = "No valid response from OpenAI.", IsSuccess = true };
                }

                var promptTokens = result.Usage.prompt_tokens;
                var completionTokens = result.Usage.completion_tokens;
                var totalTokens = result.Usage.total_tokens;

                // Use the input and output prices from the database
                decimal inputCost = rate.InputPrice;
                decimal outputCost = rate.OutputPrice;
                decimal currentCost = (promptTokens * inputCost / 1000) + (completionTokens * outputCost / 1000);

                return new PitchResult
                {
                    Content = result.Choices[0].Message.Content,
                    PromptTokens = promptTokens,
                    CompletionTokens = completionTokens,
                    TotalTokens = totalTokens,
                    CurrentCost = currentCost,
                    IsSuccess = true
                };
            }
            catch (HttpRequestException ex)
            {
                return new PitchResult { Content = $"Request to OpenAI failed: {ex.Message}", IsSuccess = false };
            }
        }

       



    }
}
