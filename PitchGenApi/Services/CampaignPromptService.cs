using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using PitchGenApi.Model;
using PitchGenApi.Models;
using PitchGenApi.Model.DTOs;
using PitchGenApi.Database;
using Microsoft.EntityFrameworkCore; // ✅ Needed for async EF methods



namespace PitchGenApi.Services
{
    public class CampaignPromptService
    {
        private readonly HttpClient _httpClient;
        private readonly string _apiKey;

        // Store sessions (chat history per user)
        private static Dictionary<string, CampaignSession> _sessions = new();

        // Store edit sessions separately
        private static Dictionary<string, EditSession> _editSessions = new();

        private readonly AppDbContext _dbContext;   // ✅ Add this

        public CampaignPromptService(
            HttpClient httpClient,
            IOptions<OpenAISettings> options,
            AppDbContext dbContext) // ✅ Inject it
        {
            _httpClient = httpClient;
            _apiKey = options.Value.ApiKey;
            _dbContext = dbContext;  // ✅ assign it

            _httpClient.Timeout = TimeSpan.FromMinutes(5);
            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");
        }

        // ========================================
        // REGULAR CHAT METHODS
        // ========================================

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

            return await SendToGptAsync(_sessions[userId].Messages, model, userId, false);
        }

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

        public void ClearChatHistory(string userId)
        {
            if (_sessions.ContainsKey(userId))
                _sessions.Remove(userId);
        }

        // ========================================
        // EDIT MODE METHODS
        // ========================================

        public async Task<object> StartEditConversationAsync(
            string userId,
            int campaignTemplateId,
            string placeholder,
            string currentValue,
            string editInstructions,
            List<ConversationMessage> oldMessages,
            Dictionary<string, string> placeholderValues,
            string model)
        {
            // Create session key combining userId and templateId
            string sessionKey = $"{userId}_{campaignTemplateId}";

            // Initialize edit session
            var editSession = new EditSession
            {
                UserId = userId,
                CampaignTemplateId = campaignTemplateId,
                EditingPlaceholder = placeholder,
                OriginalPlaceholderValues = placeholderValues ?? new Dictionary<string, string>(),
                Messages = new List<Dictionary<string, string>>()
            };

            // Build context message from old conversation
            string contextMessage = BuildContextFromOldConversation(oldMessages, placeholder, placeholderValues);

            // Add system prompt with context
            editSession.Messages.Add(new Dictionary<string, string>
            {
                { "role", "system" },
                { "content", $"{editInstructions}\n\n{contextMessage}" }
            });

            // Add initial user message
            string initialMessage = $"I want to change the value of {{{placeholder}}}. Current value is: \"{currentValue}\"";
            editSession.Messages.Add(new Dictionary<string, string>
            {
                { "role", "user" },
                { "content", initialMessage }
            });

            // Store session
            _editSessions[sessionKey] = editSession;

            // Get AI response
            return await SendToGptAsync(editSession.Messages, model, sessionKey, true);
        }

        public async Task<object> ContinueEditConversationAsync(
            string userId,
            int campaignTemplateId,
            string message,
            string model)
        {
            string sessionKey = $"{userId}_{campaignTemplateId}";

            if (!_editSessions.ContainsKey(sessionKey))
            {
                return new
                {
                    assistantText = "⚠️ Edit session not found. Please start a new edit conversation.",
                    error = true
                };
            }

            var editSession = _editSessions[sessionKey];

            // Add user message
            editSession.Messages.Add(new Dictionary<string, string>
            {
                { "role", "user" },
                { "content", message }
            });

            return await SendToGptAsync(editSession.Messages, model, sessionKey, true);
        }

        public void ClearEditSession(string userId, int campaignTemplateId)
        {
            string sessionKey = $"{userId}_{campaignTemplateId}";
            if (_editSessions.ContainsKey(sessionKey))
                _editSessions.Remove(sessionKey);
        }

        // ========================================
        // HELPER METHODS
        // ========================================

        private string BuildContextFromOldConversation(
            List<ConversationMessage> oldMessages,
            string editingPlaceholder,
            Dictionary<string, string> placeholderValues)
        {
            if (oldMessages == null || oldMessages.Count == 0)
            {
                return "ORIGINAL CONTEXT: This is a new campaign with no previous conversation.";
            }

            var contextBuilder = new StringBuilder();
            contextBuilder.AppendLine("=== ORIGINAL CONVERSATION CONTEXT ===");
            contextBuilder.AppendLine("Here's the original conversation where this campaign was created:");
            contextBuilder.AppendLine();

            // Include relevant parts of old conversation (limit to avoid token limits)
            int messageCount = 0;
            foreach (var msg in oldMessages)
            {
                if (messageCount >= 10) break; // Limit to last 10 messages for context

                string role = msg.Type == "user" ? "User" : "Assistant";
                contextBuilder.AppendLine($"{role}: {msg.Content}");
                contextBuilder.AppendLine();
                messageCount++;
            }

            contextBuilder.AppendLine("=== CURRENT PLACEHOLDER VALUES ===");
            if (placeholderValues != null && placeholderValues.Count > 0)
            {
                foreach (var kvp in placeholderValues)
                {
                    string marker = kvp.Key == editingPlaceholder ? " ← EDITING THIS" : "";
                    contextBuilder.AppendLine($"{{{kvp.Key}}}: {kvp.Value}{marker}");
                }
            }
            contextBuilder.AppendLine("=== END OF CONTEXT ===");
            contextBuilder.AppendLine();

            return contextBuilder.ToString();
        }

        // ========================================
        // GPT API COMMUNICATION
        // ========================================

        private async Task<object> SendToGptAsync(
            List<Dictionary<string, string>> messages,
            string model,
            string sessionKey,
            bool isEditMode = false)
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
                    input = inputMessages,
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
                if (isEditMode)
                {
                    // Check for edit completion marker
                    bool hasUpdateSection = aiResponse.Contains("==PLACEHOLDER_UPDATE_START==")
                                           && aiResponse.Contains("==PLACEHOLDER_UPDATE_END==");

                    if (hasUpdateSection)
                    {
                        Console.WriteLine("Edit completion markers detected!");

                        // Clear the edit session
                        if (_editSessions.ContainsKey(sessionKey))
                            _editSessions.Remove(sessionKey);

                        return new
                        {
                            isComplete = true,
                            assistantText = aiResponse,
                            fullResponse = result,
                            sessionActive = false
                        };
                    }
                }
                else
                {
                    // Regular completion check
                    bool hasPlaceholderSection = aiResponse.Contains("==PLACEHOLDER_VALUES_START==")
                                               && aiResponse.Contains("==PLACEHOLDER_VALUES_END==");
                    bool hasCompletionJson = aiResponse.Contains("\"status\"")
                                           && aiResponse.Contains("\"complete\"");

                    if (hasPlaceholderSection && hasCompletionJson)
                    {
                        Console.WriteLine("Completion markers detected!");

                        // Clear the regular session
                        if (_sessions.ContainsKey(sessionKey))
                            _sessions.Remove(sessionKey);

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
            }

            // Save assistant reply to history
            if (!string.IsNullOrWhiteSpace(aiResponse))
            {
                if (isEditMode && _editSessions.ContainsKey(sessionKey))
                {
                    _editSessions[sessionKey].Messages.Add(new Dictionary<string, string>
                    {
                        { "role", "assistant" },
                        { "content", aiResponse }
                    });
                }
                else if (!isEditMode && _sessions.ContainsKey(sessionKey))
                {
                    _sessions[sessionKey].Messages.Add(new Dictionary<string, string>
                    {
                        { "role", "assistant" },
                        { "content", aiResponse }
                    });
                }
                // NEW: 🆕 Extract placeholder values after each AI message
                var placeholderValues = ExtractPlaceholderValues(aiResponse);
                if (placeholderValues.Count > 0)
                {
                    await SavePartialPlaceholderValues(sessionKey, placeholderValues);
                }
            }

            // Return non-complete response
            return new
            {
                isComplete = false,
                assistantText = aiResponse ?? "I'm sorry, I encountered an issue. Please try again.",
                fullResponse = result,
                sessionActive = true,
                messageCount = isEditMode && _editSessions.ContainsKey(sessionKey)
                    ? _editSessions[sessionKey].Messages.Count
                    : (!isEditMode && _sessions.ContainsKey(sessionKey) ? _sessions[sessionKey].Messages.Count : 0)
            };
        }

        // ========================================
        // NESTED CLASSES
        // ========================================

        private Dictionary<string, string> ExtractPlaceholderValues(string aiResponse)
        {
            var placeholders = new Dictionary<string, string>();
            if (string.IsNullOrWhiteSpace(aiResponse))
                return placeholders;

            var match = Regex.Match(aiResponse, @"==PLACEHOLDER_VALUES_START==([\s\S]*?)==PLACEHOLDER_VALUES_END==");
            if (match.Success)
            {
                var section = match.Groups[1].Value;
                var lineRegex = new Regex(@"\{([^}]+)\}\s*=\s*(.*)");
                foreach (Match line in lineRegex.Matches(section))
                {
                    var key = line.Groups[1].Value.Trim();
                    var value = line.Groups[2].Value.Trim();
                    placeholders[key] = value;
                }
            }

            return placeholders;
        }

        private async Task SavePartialPlaceholderValues(string sessionKey, Dictionary<string, string> placeholderValues)
        {
            try
            {
                // Extract userId and templateId if this is an edit session
                string userId;
                int? campaignTemplateId = null;

                if (_editSessions.ContainsKey(sessionKey))
                {
                    var session = _editSessions[sessionKey];
                    userId = session.UserId;
                    campaignTemplateId = session.CampaignTemplateId;
                }
                else if (_sessions.ContainsKey(sessionKey))
                {
                    userId = _sessions[sessionKey].UserId;
                }
                else
                {
                    return;
                }

                if (campaignTemplateId.HasValue)
                {
                    // ✅ Update CampaignTemplate record in DB dynamically
                    var template = await _dbContext.CampaignTemplates
                        .FirstOrDefaultAsync(t => t.Id == campaignTemplateId);

                    if (template != null)
                    {
                        Dictionary<string, string>? existing = null;
                        if (!string.IsNullOrEmpty(template.PlaceholderValues))
                        {
                            existing = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(template.PlaceholderValues);
                        }
                        existing ??= new Dictionary<string, string>();

                        foreach (var kvp in placeholderValues)
                            existing[kvp.Key] = kvp.Value;

                        template.PlaceholderValues = System.Text.Json.JsonSerializer.Serialize(existing);
                        template.UpdatedAt = DateTime.UtcNow;

                        await _dbContext.SaveChangesAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error saving partial placeholder values: {ex.Message}");
            }
        }


        public class EditSession
        {
            public string UserId { get; set; }
            public int CampaignTemplateId { get; set; }
            public string EditingPlaceholder { get; set; }
            public List<Dictionary<string, string>> Messages { get; set; } = new();
            public Dictionary<string, string> OriginalPlaceholderValues { get; set; }
        }

        public class CompletionResponse
        {
            [JsonProperty("status")]
            public string Status { get; set; }

            [JsonProperty("final_prompt")]
            public string FinalPrompt { get; set; }
        }
    }

    // ========================================
    // WEB SEARCH SERVICE
    // ========================================

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