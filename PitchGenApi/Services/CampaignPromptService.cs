using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using PitchGenApi.Database;
using PitchGenApi.Model;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using PitchGenApi.Model.DTOs;
using PitchGenApi.Models;

namespace PitchGenApi.Services
{
    public class CampaignPromptService
    {
        private readonly HttpClient _httpClient;
        private readonly string _apiKey;
        private readonly IServiceScopeFactory _scopeFactory; // scope factory for DbContext

        // Store sessions (chat history per user)
        public static Dictionary<string, CampaignSession> _sessions = new();

        public class CampaignSession
        {
            public string UserId { get; set; } = string.Empty;
            public int CampaignTemplateId { get; set; }
            public List<Dictionary<string, string>> Messages { get; set; } = new();
        }

        public CampaignPromptService(
            HttpClient httpClient,
            IOptions<OpenAISettings> options,
            IServiceScopeFactory scopeFactory)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _apiKey = options?.Value?.ApiKey ?? throw new ArgumentNullException(nameof(options));
            _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));

            _httpClient.Timeout = TimeSpan.FromMinutes(5);
            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");
            _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
        }

        // Single method to handle both start and continue
        public async Task<object> ProcessChatAsync(string userId, string message, string systemPrompt, string model)
        {
            if (string.IsNullOrWhiteSpace(userId))
                return new { assistantText = "⚠️ UserId is required", error = true };

            // Create a clean session if not exists
            if (!_sessions.ContainsKey(userId))
            {
                _sessions[userId] = new CampaignSession
                {
                    UserId = userId,
                    CampaignTemplateId = 0,   // ❌ DO NOT RESTORE OLD CAMPAIGN
                    Messages = new List<Dictionary<string, string>>()
                };

                // If system prompt provided → add it once
                if (!string.IsNullOrWhiteSpace(systemPrompt))
                {
                    _sessions[userId].Messages.Add(new Dictionary<string, string>
            {
                { "role", "system" },
                { "content", systemPrompt }
            });
                }
                else
                {
                    return new
                    {
                        assistantText = "⚠️ System prompt is required to start a new conversation.",
                        requiresSystemPrompt = true
                    };
                }
            }

            // Validate message
            if (string.IsNullOrWhiteSpace(message))
            {
                return new
                {
                    assistantText = "⚠️ Message is required for continuing the conversation."
                };
            }

            var session = _sessions[userId];

            // Add user message
            session.Messages.Add(new Dictionary<string, string>
    {
        { "role", "user" },
        { "content", message }
    });

            // Send to GPT
            var response = await SendToGptAsync(session.Messages, model, userId);

            return response;
        }

        // Get chat history
        public object GetChatHistory(string userId)
        {
            if (!_sessions.ContainsKey(userId))
                return null;

            return new
            {
                userId,
                messages = _sessions[userId].Messages,
                messageCount = _sessions[userId].Messages.Count
            };
        }

        // Clear chat history
        public void ClearChatHistory(string userId)
        {
            if (_sessions.ContainsKey(userId))
                _sessions.Remove(userId);
        }

        // --------------------------
        // Responses API integration
        // --------------------------
        private async Task<object> SendToGptAsync(List<Dictionary<string, string>> messages, string model, string userId)
        {
            if (string.IsNullOrWhiteSpace(model))
                model = "gpt-4o";

            // Build a single role-prefixed input string for Responses API
            var sbInput = new StringBuilder();

            if (messages == null || messages.Count == 0)
            {
                sbInput.AppendLine("user: ");
            }
            else
            {
                foreach (var m in messages)
                {
                    var role = m.ContainsKey("role") ? m["role"] : "user";
                    var msgContent = m.ContainsKey("content") ? m["content"] ?? string.Empty : string.Empty;
                    sbInput.AppendLine($"{role}: {msgContent}");
                }
            }

            var requestBody = new Dictionary<string, object>
            {
                { "model", model },
                { "input", sbInput.ToString() },
                { "temperature", 1.0 },
                { "max_output_tokens", 15000 },
                { "tools", new object[] { new { type = "web_search_preview" } } }
            };

            var requestJson = JsonConvert.SerializeObject(requestBody);
            var httpContent = new StringContent(requestJson, Encoding.UTF8, "application/json");

            try
            {
                var endpoint = "https://api.openai.com/v1/responses";
                var httpResponse = await _httpClient.PostAsync(endpoint, httpContent);
                var raw = await httpResponse.Content.ReadAsStringAsync();

                if (!httpResponse.IsSuccessStatusCode)
                {
                    return new { assistantText = $"API Error: {httpResponse.StatusCode}", rawResponse = raw, error = true };
                }

                var parsed = JsonConvert.DeserializeObject<JObject>(raw)!; // non-null after success

                // Prefer output_text
                string aiResponse = parsed["output_text"]?.ToString() ?? string.Empty;

                if (string.IsNullOrWhiteSpace(aiResponse))
                {
                    aiResponse = ExtractTextFromOutputs(parsed);
                }

                if (string.IsNullOrWhiteSpace(aiResponse))
                    aiResponse = "⚠️ No response from GPT (empty content).";

                // Extract placeholders & save to DB if present
                var updatedPlaceholders = ExtractPlaceholders(aiResponse);
                if (updatedPlaceholders.Count > 0)
                {
                    try
                    {
                        await SavePlaceholderValuesToDb(userId, updatedPlaceholders);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[DB SAVE ERROR] {ex.Message}");
                    }
                }

                // Ensure session exists
                if (!_sessions.ContainsKey(userId))
                {
                    _sessions[userId] = new CampaignSession { UserId = userId, CampaignTemplateId = 0, Messages = new List<Dictionary<string, string>>() };
                }

                _sessions[userId].Messages.Add(new Dictionary<string, string>
                {
                    { "role", "assistant" },
                    { "content", aiResponse }
                });

                // Collect web search sources if present
                var webSearchResults = new List<object>();
                try
                {
                    var outputs = parsed["output"] as JArray;
                    if (outputs != null)
                    {
                        foreach (var outItem in outputs)
                        {
                            var action = outItem["action"];
                            if (action != null && action["sources"] is JArray sources)
                            {
                                foreach (var s in sources)
                                {
                                    webSearchResults.Add(new
                                    {
                                        url = s["url"]?.ToString(),
                                        title = s["title"]?.ToString(),
                                        snippet = s["snippet"]?.ToString()
                                    });
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WEB PARSE ERROR] {ex.Message}");
                }

                return new
                {
                    isComplete = aiResponse.Contains("==PLACEHOLDER_VALUES_START==") &&
                                 aiResponse.Contains("==PLACEHOLDER_VALUES_END==") &&
                                 aiResponse.Contains("\"complete\""),
                    assistantText = aiResponse,
                    fullResponse = parsed,
                    sessionActive = true,
                    messageCount = _sessions[userId].Messages.Count,
                    webSearchResults
                };
            }

            catch (Exception ex)
            {
                await SaveConversationToDb(userId);

                Console.WriteLine($"[SEND_TO_GPT ERROR] {ex.Message}");
                return new { assistantText = $"⚠️ GPT request failed - {ex.Message}", error = true };
            }
        }

        private string ExtractTextFromOutputs(JObject parsed)
        {
            var outputs = parsed["output"] as JArray;
            if (outputs == null) return string.Empty;

            var sb = new StringBuilder();

            foreach (var outItem in outputs)
            {
                var contentArray = outItem["content"] as JArray;
                if (contentArray != null)
                {
                    foreach (var c in contentArray)
                    {
                        var text = c["text"]?.ToString();
                        if (!string.IsNullOrWhiteSpace(text))
                            sb.AppendLine(text.Trim());
                    }
                }
            }

            return sb.ToString().Trim();
        }

        // Parse placeholder markers
        private Dictionary<string, string> ExtractPlaceholders(string aiResponse)
        {
            var dict = new Dictionary<string, string>();
            if (string.IsNullOrWhiteSpace(aiResponse)) return dict;

            var blockMatch = Regex.Match(aiResponse,
                "==PLACEHOLDER_VALUES_START==(.*?)==PLACEHOLDER_VALUES_END==",
                RegexOptions.Singleline);

            if (!blockMatch.Success) return dict;

            var block = blockMatch.Groups[1].Value;

            // Matches:
            // {placeholder} = any text INCLUDING newline until next {xxx} OR END
            var regex = new Regex(@"\{([^}]+)\}\s*=\s*((?s).*?)(?=\n\{[^}]+\}\s*=|$)", RegexOptions.Singleline);

            foreach (Match m in regex.Matches(block))
            {
                var key = m.Groups[1].Value.Trim();
                var val = m.Groups[2].Value.Trim();

                val = val.Replace("\\n", "\n"); // convert escaped \n to real newlines

                dict[key] = val;

            }

            return dict;
        }

        // Save to DB using injected IServiceScopeFactory
        private async Task SavePlaceholderValuesToDb(string userId, Dictionary<string, string> newValues)
        {
            var session = _sessions.ContainsKey(userId) ? _sessions[userId] : null;
            if (session == null)
            {
                Console.WriteLine($"⚠️  No active session found for user {userId}. Skipping DB update.");
                return;
            }

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var campaign = await db.CampaignTemplates
                .Include(c => c.TemplateDefinition)
                .FirstOrDefaultAsync(c => c.Id == session.CampaignTemplateId);

            if (campaign == null)
            {
                Console.WriteLine($"⚠️  Campaign not found for {session.CampaignTemplateId}");
                return;
            }

            var existing = string.IsNullOrEmpty(campaign.PlaceholderValues)
                ? new Dictionary<string, string>()
                : JsonConvert.DeserializeObject<Dictionary<string, string>>(campaign.PlaceholderValues) ?? new Dictionary<string, string>();

            foreach (var kv in newValues)
                existing[kv.Key] = kv.Value;

            campaign.PlaceholderValues = JsonConvert.SerializeObject(existing);
            campaign.PlaceholderListWithValue = string.Join("\n", existing.Select(kv => $"{{{kv.Key}}} = {kv.Value}"));

            try
            {
                string unpopulated = campaign.TemplateDefinition?.MasterBlueprintUnpopulated ?? string.Empty;
                string filledBlueprint = unpopulated;

                foreach (var (key, value) in existing)
                {
                    if (string.IsNullOrWhiteSpace(key)) continue;
                    filledBlueprint = Regex.Replace(
                        filledBlueprint,
                        $"{{{Regex.Escape(key)}}}",
                        value ?? string.Empty,
                        RegexOptions.IgnoreCase);
                }

                campaign.CampaignBlueprint = filledBlueprint;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Blueprint Build Error] {ex.Message}");
            }

            campaign.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        public class CompletionResponse
        {
            [JsonProperty("status")]
            public string Status { get; set; } = string.Empty;
            [JsonProperty("final_prompt")]
            public string FinalPrompt { get; set; } = string.Empty;
        }

        // Generate example output using Responses API (with web search) - always uses input
        public async Task<string?> GenerateExampleOutputAsync(
            Dictionary<string, string> placeholderValues,
            string masterTemplate,
            string model = "gpt-5")
        {
            if (placeholderValues == null || placeholderValues.Count == 0)
                return null;

            // 1) Fill placeholders
            string filledTemplate = masterTemplate ?? string.Empty;
            foreach (var (key, value) in placeholderValues)
                filledTemplate = filledTemplate.Replace($"{{{key}}}", value ?? string.Empty, StringComparison.OrdinalIgnoreCase);

            // 2) Build role-prefixed input (same style as SendToGptAsync)
            var sbInput = new StringBuilder();
            sbInput.AppendLine("system: You are the example output generator. Produce clean HTML only.");
            sbInput.AppendLine($"user: {filledTemplate}");

            // 3) Attempt with tools first, then without tools
            foreach (var useTools in new[] { true, false })
            {
                var requestData = new Dictionary<string, object>
        {
            { "model", model },
            { "input", sbInput.ToString() },
            { "temperature", 0.3 },
            { "max_output_tokens", 15000 }
        };

                if (useTools)
                {
                    requestData["tools"] = new object[] { new { type = "web_search_preview" } };
                    requestData["text"] = new { verbosity = "low" };
                    Console.WriteLine($"[GenerateExampleOutputAsync] Attempt WITH tools for model={model}");
                }
                else
                {
                    Console.WriteLine($"[GenerateExampleOutputAsync] Attempt WITHOUT tools for model={model}");
                }

                var json = JsonConvert.SerializeObject(requestData);
                var httpContent = new StringContent(json, Encoding.UTF8, "application/json");

                try
                {
                    var response = await _httpClient.PostAsync("https://api.openai.com/v1/responses", httpContent);
                    var raw = await response.Content.ReadAsStringAsync();

                    // Log the raw response for debugging (important for model-specific quirks)
                    Console.WriteLine($"[GenerateExampleOutputAsync] RAW ({model}, tools={useTools}): {raw}");

                    if (!response.IsSuccessStatusCode)
                    {
                        // If tools were used and API complains about unsupported parameter, retry without tools
                        if (useTools && raw.IndexOf("Unsupported parameter", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            Console.WriteLine($"[GenerateExampleOutputAsync] Tools unsupported by model={model}; retrying without tools.");
                            continue; // try next iteration without tools
                        }

                        Console.WriteLine($"[GenerateExampleOutputAsync] API Error {response.StatusCode} (tools={useTools}). Raw: {raw}");
                        if (!useTools) return null;
                        continue;
                    }

                    // Parse success
                    var parsed = JsonConvert.DeserializeObject<JObject>(raw)!;
                    string resultText = parsed["output_text"]?.ToString() ?? string.Empty;

                    if (string.IsNullOrWhiteSpace(resultText))
                        resultText = ExtractTextFromOutputs(parsed);

                    // If we got content, return it
                    if (!string.IsNullOrWhiteSpace(resultText))
                    {
                        Console.WriteLine($"[GenerateExampleOutputAsync] Received output (tools={useTools})");
                        return resultText.Trim();
                    }

                    // If 200 OK but empty and we used tools — retry without tools
                    if (useTools)
                    {
                        Console.WriteLine($"[GenerateExampleOutputAsync] Empty output with tools for model={model}. Retrying without tools.");
                        continue;
                    }

                    // No tools and still empty => give up
                    Console.WriteLine($"[GenerateExampleOutputAsync] Empty output without tools for model={model}. Giving up.");
                    return null;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[GenerateExampleOutputAsync] Exception (tools={useTools}): {ex.Message}");
                    if (useTools) continue;
                    return null;
                }
            }

            return null;
        }


        private async Task SaveConversationToDb(string userId)
        {
            if (!_sessions.ContainsKey(userId)) return;

            var session = _sessions[userId];
            if (session.CampaignTemplateId == 0) return;

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Load existing conversation row
            var existing = await db.CampaignConversations
                .FirstOrDefaultAsync(x => x.CampaignTemplateId == session.CampaignTemplateId);

            // Convert NEW messages coming from in-memory session
            var newMessages = session.Messages.Select(m => new
            {
                Role = m.ContainsKey("role") ? m["role"] : "assistant",
                Content = m.ContainsKey("content") ? m["content"] : string.Empty,
                Timestamp = DateTime.UtcNow
            }).ToList();

            // If no conversation exists, create a new conversation row
            if (existing == null)
            {
                var json = JsonConvert.SerializeObject(newMessages);

                existing = new CampaignConversation
                {
                    ClientId = session.UserId,
                    CampaignTemplateId = session.CampaignTemplateId,
                    ConversationData = json,
                    Model = "gpt-5",
                    StartedAt = DateTime.UtcNow,
                    Mode = "new",
                    EditNumber = 0,
                    IsComplete = false
                };

                db.CampaignConversations.Add(existing);
            }
            else
            {
                // APPEND MODE — MERGE OLD + NEW
                List<object> oldMessages = new();

                if (!string.IsNullOrWhiteSpace(existing.ConversationData))
                {
                    try
                    {
                        oldMessages = JsonConvert.DeserializeObject<List<object>>(existing.ConversationData)
                                      ?? new List<object>();
                    }
                    catch
                    {
                        oldMessages = new List<object>();
                    }
                }

                // Combine old + new
                var combined = oldMessages.Concat(newMessages).ToList();

                // Save back
                existing.ConversationData = JsonConvert.SerializeObject(combined);
                existing.CompletedAt = DateTime.UtcNow;
            }

            await db.SaveChangesAsync();
        }


        public async Task<object> StartEditModeAsync(StartEditConversationRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.UserId))
                return new { assistantText = "UserId is required", error = true };

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Load ONLY template + definition (❌ no conversation)
            var campaign = await db.CampaignTemplates
                .Include(c => c.TemplateDefinition)
                .FirstOrDefaultAsync(c => c.Id == req.CampaignTemplateId);

            if (campaign == null)
                return new { assistantText = "Campaign not found", error = true };

            // Load placeholder values
            var placeholders = string.IsNullOrEmpty(campaign.PlaceholderValues)
                ? new Dictionary<string, string>()
                : JsonConvert.DeserializeObject<Dictionary<string, string>>(campaign.PlaceholderValues)
                  ?? new Dictionary<string, string>();

            // ❌ Do NOT load or update conversation table
            // ❌ Do NOT increment EditNumber
            // ❌ Do NOT open or fetch old conversation

            // Create clean in-memory session
            _sessions[req.UserId] = new CampaignSession
            {
                UserId = req.UserId,
                CampaignTemplateId = req.CampaignTemplateId,
                Messages = new List<Dictionary<string, string>>()
            };

            // Build system prompt with AIInstructionsForEdit + placeholder values
            var sys = new StringBuilder();
            sys.AppendLine(campaign.TemplateDefinition.AIInstructionsForEdit);
            sys.AppendLine("\nHere are the existing placeholder values:");

            foreach (var p in placeholders)
                sys.AppendLine($"{{{p.Key}}} = {p.Value}");

            // Add system message to session
            _sessions[req.UserId].Messages.Add(new Dictionary<string, string>
    {
        { "role", "system" },
        { "content", sys.ToString() }
    });

            // Add user edit request
            _sessions[req.UserId].Messages.Add(new Dictionary<string, string>
    {
        { "role", "user" },
        { "content", $"I want to edit the placeholder: {req.Placeholder}.\nCurrent value: {req.CurrentValue}" }
    });

            // Send to GPT
            return await SendToGptAsync(_sessions[req.UserId].Messages, req.Model ?? "gpt-5.1", req.UserId);
        }

        public async Task<object> ContinueEditModeAsync(EditChatRequest req)
        {
            if (!_sessions.ContainsKey(req.UserId))
                return new { assistantText = "No active edit session. Call /edit/start first.", error = true };

            if (string.IsNullOrWhiteSpace(req.Message))
                return new { assistantText = "Message is required", error = true };

            var session = _sessions[req.UserId];

            // If edit flow already completed, do NOT continue the conversation
            var lastMessage = session.Messages.LastOrDefault();
            if (lastMessage != null &&
                lastMessage.ContainsKey("content") &&
                lastMessage["content"].Contains("==PLACEHOLDER_VALUES_START==") &&
                lastMessage["content"].Contains("\"complete\""))
            {
                return new
                {
                    assistantText = "This edit session is already completed. Start a new edit to modify another placeholder.",
                    isComplete = true,
                    error = false
                };
            }

            // Push user message into session
            session.Messages.Add(new Dictionary<string, string>
                {
                    { "role", "user" },
                    { "content", req.Message }
                });

            // Send to GPT
            return await SendToGptAsync(session.Messages, req.Model ?? "gpt-5.1", req.UserId);
        }

    }

}
