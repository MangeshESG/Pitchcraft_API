using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using PitchGenApi.Model;
using PitchGenApi.Models;
using static PitchGenApi.Model.ChatGptResponse;

namespace PitchGenApi.Services
{
    public class CampaignPromptService
    {
        private readonly HttpClient _httpClient;
        private readonly string _apiKey;

        private static Dictionary<string, CampaignSession> _sessions = new();

        public CampaignPromptService(HttpClient httpClient, IOptions<OpenAISettings> options)
        {
            _httpClient = httpClient;
            _apiKey = options.Value.ApiKey;
            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");
        }

        // ✅ Add missing method from controller
        public async Task<string> StartCampaignAsync(string userId, string systemPrompt, string model = "gpt-4o-mini")
        {
            if (_sessions.ContainsKey(userId))
                _sessions.Remove(userId);

            // Reset + add system instruction (your Master Prompt)
            _sessions[userId] = new CampaignSession
            {
                UserId = userId,
                Messages = new List<Dictionary<string, string>>
        {
            new Dictionary<string,string>
            {
                { "role", "system" },
                { "content", systemPrompt }
            }
        }
            };

            // Kick-off: Ask first question ("What does vendor_company do?")
            var requestData = new
            {
                model,
                messages = _sessions[userId].Messages,
                temperature = 0.7,
                max_completion_tokens = 300
            };

            var response = await _httpClient.PostAsync(
                "https://api.openai.com/v1/chat/completions",
                new StringContent(JsonConvert.SerializeObject(requestData), Encoding.UTF8, "application/json")
            );

            var result = JsonConvert.DeserializeObject<ChatCompletionResponse>(
                await response.Content.ReadAsStringAsync()
            );

            var aiResponse = result?.Choices?.FirstOrDefault()?.Message?.Content ??
                             "No response (check Master Prompt formatting).";

            // Save first question into session history
            _sessions[userId].Messages.Add(new Dictionary<string, string>
    {
        { "role", "assistant" },
        { "content", aiResponse }
    });

            return aiResponse;
        }
        // Continue Q&A
        public async Task<string> CampaignChatAsync(string userId, string userMessage, string model = "gpt-4o-mini")
        {
            if (!_sessions.ContainsKey(userId))
                return "No active campaign. Start first.";

            var session = _sessions[userId];
            session.Messages.Add(new Dictionary<string, string> { { "role", "user" }, { "content", userMessage } });

            var requestData = new
            {
                model,
                messages = session.Messages,
                temperature = 0.7,
                max_completion_tokens = 500
            };

            var response = await _httpClient.PostAsync("https://api.openai.com/v1/chat/completions",
                new StringContent(JsonConvert.SerializeObject(requestData), Encoding.UTF8, "application/json"));

            var result = JsonConvert.DeserializeObject<ChatCompletionResponse>(await response.Content.ReadAsStringAsync());

            var aiResponse = result?.Choices?.FirstOrDefault()?.Message?.Content ?? "No response";
            session.Messages.Add(new Dictionary<string, string> { { "role", "assistant" }, { "content", aiResponse } });

            if (aiResponse.Contains("Campaign Master Prompt"))
                session.CampaignPrompt = aiResponse;

            return aiResponse;
        }

        // ✅ Keep only one GenerateSampleEmailAsync - now uses request.InstructionMessage
        public async Task<string> GenerateSampleEmailAsync(string userId, Contact contact, string instructionMessage, string model = "gpt-4o-mini")
        {
            if (!_sessions.ContainsKey(userId) || string.IsNullOrWhiteSpace(_sessions[userId].CampaignPrompt))
                return "No Campaign Prompt found. Build campaign prompt first.";

            var campaignPrompt = _sessions[userId].CampaignPrompt;

            var userMessage = $@"
Using this Campaign Prompt:
{campaignPrompt}

{instructionMessage}

Contact record provided:
- FullName: {contact.full_name}
- CompanyName: {contact.company_name}
- JobTitle: {contact.job_title}
- Email: {contact.email}
- Country: {contact.country_or_address}
";

            var requestData = new
            {
                model,
                messages = new object[]
                {
                new { role = "system", content = "You are an expert email generator." },
                new { role = "user", content = userMessage }
                },
                temperature = 0.7,
                max_completion_tokens = 400
            };

            var response = await _httpClient.PostAsync("https://api.openai.com/v1/chat/completions",
                new StringContent(JsonConvert.SerializeObject(requestData), Encoding.UTF8, "application/json"));

            var result = JsonConvert.DeserializeObject<ChatCompletionResponse>(await response.Content.ReadAsStringAsync());

            var emailDraft = result?.Choices?.FirstOrDefault()?.Message?.Content ?? "No draft generated";
            _sessions[userId].DraftEmail = emailDraft;

            return emailDraft;
        }

        public async Task<string> RefinePromptWithFeedbackAsync(string userId, string feedback, string model = "gpt-4o-mini")
        {
            if (!_sessions.ContainsKey(userId) || string.IsNullOrWhiteSpace(_sessions[userId].CampaignPrompt))
                return "No Campaign Prompt to refine.";

            var currentPrompt = _sessions[userId].CampaignPrompt;
            var userMessage = $"Here is the current Campaign Prompt:\n{currentPrompt}\nUser feedback: {feedback}";

            var requestData = new
            {
                model,
                messages = new object[]
                {
                new { role = "system", content = "You refine campaign prompts based on feedback. Keep {{FullName}}, {{CompanyName}} placeholders." },
                new { role = "user", content = userMessage }
                },
                temperature = 0.7,
                max_completion_tokens = 400
            };

            var response = await _httpClient.PostAsync("https://api.openai.com/v1/chat/completions",
                new StringContent(JsonConvert.SerializeObject(requestData), Encoding.UTF8, "application/json"));

            var result = JsonConvert.DeserializeObject<ChatCompletionResponse>(await response.Content.ReadAsStringAsync());

            var refinedPrompt = result?.Choices?.FirstOrDefault()?.Message?.Content ?? currentPrompt;
            _sessions[userId].CampaignPrompt = refinedPrompt;

            return refinedPrompt;
        }

        public string ApproveCampaign(string userId)
        {
            if (!_sessions.ContainsKey(userId)) return "No campaign.";

            var session = _sessions[userId];
            session.IsApproved = true;
            return session.CampaignPrompt ?? "";
        }
    }
}