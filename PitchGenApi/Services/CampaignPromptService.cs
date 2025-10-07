using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
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
            _httpClient.Timeout = TimeSpan.FromMinutes(5);
            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");
        }

        // ✅ Single method to handle both start and continue
        public async Task<object> ProcessChatAsync(string userId, string message, string systemPrompt, string model)
        {
            // Check if this is a new conversation or continuing existing one
            if (!_sessions.ContainsKey(userId))
            {
                // New conversation - initialize with system prompt
                if (string.IsNullOrWhiteSpace(systemPrompt))
                {
                    return new
                    {
                        assistantText = "⚠️ System prompt is required for starting a new campaign.",
                        requiresSystemPrompt = true
                    };
                }

                _sessions[userId] = new CampaignSession
                {
                    UserId = userId,
                    Messages = new List<Dictionary<string, string>>
                    {
                        new Dictionary<string, string>
                        {
                            { "role", "system" },
                            { "content", systemPrompt }
                        }
                    }
                };

                // If message is provided with system prompt, add it
                if (!string.IsNullOrWhiteSpace(message))
                {
                    _sessions[userId].Messages.Add(new Dictionary<string, string>
                    {
                        { "role", "user" },
                        { "content", message }
                    });
                }
            }
            else
            {
                // Continuing existing conversation
                if (string.IsNullOrWhiteSpace(message))
                {
                    return new
                    {
                        assistantText = "⚠️ Message is required for continuing the conversation."
                    };
                }

                _sessions[userId].Messages.Add(new Dictionary<string, string>
                {
                    { "role", "user" },
                    { "content", message }
                });
            }

            return await SendToGptAsync(_sessions[userId].Messages, model, userId);
        }

        // ✅ Get chat history
        public object GetChatHistory(string userId)
        {
            if (!_sessions.ContainsKey(userId))
                return null;

            return new
            {
                userId = userId,
                messages = _sessions[userId].Messages,
                messageCount = _sessions[userId].Messages.Count
            };
        }

        // ✅ Clear chat history
        public void ClearChatHistory(string userId)
        {
            if (_sessions.ContainsKey(userId))
                _sessions.Remove(userId);
        }

        // ✅ Send to GPT and capture assistant + tool calls
        private async Task<object> SendToGptAsync(List<Dictionary<string, string>> messages, string model, string userId)
        {
            var inputMessages = messages.Select(m => new
            {
                role = m["role"],
                content = m["content"]
            }).ToList();

            // Build request data dynamically based on model
            dynamic requestData;

            // Only include reasoning parameter for GPT-5 models
            if (model.StartsWith("gpt-5"))
            {
                requestData = new
                {
                    model = model,
                    input = inputMessages,
                    reasoning = new { effort = "medium" },
                    tools = new object[] { new { type = "web_search" } },
                    tool_choice = "auto",
                    max_output_tokens = 15000,
                    temperature = 1.0
                };
            }
            else
            {
                // For GPT-4 models, use standard parameters
                requestData = new
                {
                    model = model,
                    input = inputMessages, // MUST use 'input' not 'messages'
                    tools = new object[] { new { type = "web_search" } },
                    tool_choice = "auto",
                    max_output_tokens = 15000,
                    temperature = 1.0
                };
            }

            

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
                    rawResponse = jsonResponse,
                    error = true
                };
            }

            dynamic result = JsonConvert.DeserializeObject<dynamic>(jsonResponse);

            // Extract assistant text
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

            // Check for completion markers
            if (!string.IsNullOrWhiteSpace(aiResponse))
            {
                // Look for the specific completion pattern
                bool hasPlaceholderSection = aiResponse.Contains("==PLACEHOLDER_VALUES_START==")
                                           && aiResponse.Contains("==PLACEHOLDER_VALUES_END==");
                bool hasCompletionJson = aiResponse.Contains("\"status\"")
                                       && aiResponse.Contains("\"complete\"");

                if (hasPlaceholderSection && hasCompletionJson)
                {
                    Console.WriteLine("Completion markers detected!");

                    // Clear the session
                    ClearChatHistory(userId);

                    // Return completion response
                    return new
                    {
                        isComplete = true,
                        assistantText = aiResponse,
                        fullResponse = result,
                        sessionActive = false,
                        messageCount = 0
                    };
                }
            }

            // Save assistant reply to history
            if (!string.IsNullOrWhiteSpace(aiResponse) && _sessions.ContainsKey(userId))
            {
                _sessions[userId].Messages.Add(new Dictionary<string, string>
        {
            { "role", "assistant" },
            { "content", aiResponse }
        });
            }

            // Return non-complete response
            return new
            {
                isComplete = false,
                assistantText = aiResponse ?? "I'm sorry, I encountered an issue. Please try again.",
                fullResponse = result,
                sessionActive = true,
                messageCount = _sessions.ContainsKey(userId) ? _sessions[userId].Messages.Count : 0
            };
        }
        public class CompletionResponse
        {
            [JsonProperty("status")]
            public string Status { get; set; }

            [JsonProperty("final_prompt")]
            public string FinalPrompt { get; set; }
        }

        // In your CampaignPromptService or a constants file

    }

    // Keep existing WebSearchService class as is...



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