using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using PitchGenApi.Model;
using PitchGenApi.Models;

namespace PitchGenApi.Services
{
    public class CampaignPromptService
    {
        private readonly HttpClient _httpClient;
        private readonly string _apiKey;

        // Store sessions (chat history per user)
        private static Dictionary<string, CampaignSession> _sessions = new();

        public CampaignPromptService(HttpClient httpClient, IOptions<OpenAISettings> options)
        {
            _httpClient = httpClient;
            _apiKey = options.Value.ApiKey;
            _httpClient.Timeout = TimeSpan.FromMinutes(5); // allow up to 5 min
            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");
        }

        // ✅ Start campaign
        public async Task<object> StartCampaignAsync(string userId, string systemPrompt, string model)
        {
            if (_sessions.ContainsKey(userId))
                _sessions.Remove(userId);

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

            return await SendToGptAsync(_sessions[userId].Messages, model, userId);
        }

        // ✅ Continue chat
        public async Task<object> CampaignChatAsync(string userId, string userMessage, string model)
        {
            if (!_sessions.ContainsKey(userId))
                return new { assistantText = "⚠️ No active campaign. Start a campaign first." };

            var session = _sessions[userId];
            session.Messages.Add(new Dictionary<string, string>
            {
                { "role", "user" },
                { "content", userMessage }
            });

            return await SendToGptAsync(session.Messages, model, userId);
        }

        // ✅ Send to GPT and capture assistant + tool calls
        private async Task<object> SendToGptAsync(List<Dictionary<string, string>> messages, string model, string userId)
        {
            var inputMessages = messages.Select(m => new
            {
                role = m["role"],
                content = m["content"]
            }).ToList();

            var requestData = new
            {
                model = model,
                input = inputMessages,
                reasoning = new { effort = "medium" },
                tools = new object[] { new { type = "web_search" } },
                tool_choice = "auto",
                max_output_tokens = 15000,
                temperature = 1.0
            };

            var response = await _httpClient.PostAsync(
                "https://api.openai.com/v1/responses",
                new StringContent(JsonConvert.SerializeObject(requestData), Encoding.UTF8, "application/json")
            );

            var jsonResponse = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                return new
                {
                    assistantText = $"API Error: {response.StatusCode}",
                    rawResponse = jsonResponse
                };
            }

            dynamic result = JsonConvert.DeserializeObject<dynamic>(jsonResponse);

            // Extract optional assistant text (if a "message" was returned)
            string aiResponse = null;
            if (result.output != null)
            {
                foreach (var o in result.output)
                {
                    if (o.type != null && o.type.ToString() == "message")
                    {
                        foreach (var c in o.content)
                        {
                            if (c.type != null && c.type.ToString() == "output_text")
                            {
                                aiResponse = c.text?.ToString();
                            }
                        }
                    }
                }
            }

            // ✅ Save assistant reply to history
            if (!string.IsNullOrWhiteSpace(userId) && _sessions.ContainsKey(userId))
            {
                _sessions[userId].Messages.Add(new Dictionary<string, string>
        {
            { "role", "assistant" },
            { "content", aiResponse ?? "[No text response]" }
        });
            }

            // ✅ Final structured response with ALL raw data
            return new
            {
                assistantText = aiResponse ?? "No natural language response returned.",
                fullResponse = result,        // full object graph (tool calls, sources, reasoning, etc.)
                rawJson = jsonResponse        // original JSON (safe to forward to frontend)
            };
        }
    }

    // ✅ Models for parsing web search tool responses
    public class WebSearchService
    {
        public class WebSearchResponse
        {
            [JsonProperty("output")]
            public List<OutputItem> Output { get; set; }
        }

        public class OutputItem
        {
            [JsonProperty("content")]
            public List<OutputContent> Content { get; set; }

            [JsonProperty("type")]
            public string Type { get; set; }

            [JsonProperty("status")]
            public string Status { get; set; }

            [JsonProperty("action")]
            public SearchAction Action { get; set; }
        }

        public class OutputContent
        {
            [JsonProperty("type")]
            public string Type { get; set; }

            [JsonProperty("text")]
            public string Text { get; set; }
        }

        public class SearchAction
        {
            [JsonProperty("query")]
            public string Query { get; set; }

            [JsonProperty("domains")]
            public List<string> Domains { get; set; }

            [JsonProperty("sources")]
            public List<WebSource> Sources { get; set; }
        }

        public class WebSource
        {
            [JsonProperty("url")]
            public string Url { get; set; }

            [JsonProperty("title")]
            public string Title { get; set; }

            [JsonProperty("snippet")]
            public string Snippet { get; set; }
        }
    }
}