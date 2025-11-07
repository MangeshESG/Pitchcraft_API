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

namespace PitchGenApi.Services
{
    public class CampaignPromptService
    {
        private readonly HttpClient _httpClient;
        private readonly string _apiKey;
        private readonly IServiceScopeFactory _scopeFactory;   // ✅ scope factory for DbContext

        // Store sessions (chat history per user)
        public static Dictionary<string, CampaignSession> _sessions = new();

        public class CampaignSession
        {
            public string UserId { get; set; } = string.Empty;
            public int CampaignTemplateId { get; set; }
            public List<Dictionary<string, string>> Messages { get; set; } = new();
        }

        // ✅ Constructor with IServiceScopeFactory
        public CampaignPromptService(
            HttpClient httpClient,
            IOptions<OpenAISettings> options,
            IServiceScopeFactory scopeFactory)
        {
            _httpClient = httpClient;
            _apiKey = options.Value.ApiKey;
            _scopeFactory = scopeFactory;

            _httpClient.Timeout = TimeSpan.FromMinutes(5);
            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");
        }

        // ✅ Single method to handle both start and continue
        public async Task<object> ProcessChatAsync(string userId, string message, string systemPrompt, string model)
        {
            // 🧠 Step 1. Restore session if it's missing
            if (!_sessions.ContainsKey(userId))
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                // Try to find most recent campaign for this client
                var lastCampaign = await db.CampaignTemplates
                    .OrderByDescending(c => c.CreatedAt)
                    .FirstOrDefaultAsync(c => c.ClientId == userId);

                if (lastCampaign != null)
                {
                    _sessions[userId] = new CampaignSession
                    {
                        UserId = userId,
                        CampaignTemplateId = lastCampaign.Id,
                        Messages = new List<Dictionary<string, string>>()
                    };

                    Console.WriteLine($"🧩 Restored campaign session for {userId} using campaign {lastCampaign.Id}");
                }
                else
                {
                    // 🪫 No previous campaign — need system prompt to start a new one
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
                        CampaignTemplateId = 0,
                        Messages = new List<Dictionary<string, string>>
                {
                    new() { { "role", "system" }, { "content", systemPrompt } }
                }
                    };

                    Console.WriteLine($"🆕 Created brand-new session for {userId} (no campaign found).");
                }
            }

            // 🗣 Step 2. Handle message flow
            if (string.IsNullOrWhiteSpace(message))
            {
                return new
                {
                    assistantText = "⚠️ Message is required for continuing the conversation."
                };
            }

            var session = _sessions[userId];

            // Add message to history
            if (session.Messages.All(m => m["role"] != "system"))
            {
                // add system prompt only once if missing
                if (!string.IsNullOrWhiteSpace(systemPrompt))
                {
                    session.Messages.Insert(0, new Dictionary<string, string>
            {
                { "role", "system" },
                { "content", systemPrompt }
            });
                }
            }

            session.Messages.Add(new Dictionary<string, string>
    {
        { "role", "user" },
        { "content", message }
    });

            // 🚀 Step 3. Send to GPT
            var response = await SendToGptAsync(session.Messages, model, userId);

            // 🧩 Step 4. Ensure we have campaign linkage
            if (session.CampaignTemplateId == 0)
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var campaign = await db.CampaignTemplates
                    .OrderByDescending(c => c.CreatedAt)
                    .FirstOrDefaultAsync(c => c.ClientId == userId);

                if (campaign != null)
                {
                    session.CampaignTemplateId = campaign.Id;
                    Console.WriteLine($"🔗 Linked session for {userId} to campaign {campaign.Id}");
                }
            }

            return response;
        }
        // ✅ Get chat history
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

            if (string.IsNullOrWhiteSpace(model))
            {
                model = "gpt-4o"; // Default only if not provided
            }

            // ✅ Detect if the model is GPT-5 (or newer requiring new params)
            bool isGpt5OrNewer = model.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase);

            object requestData;
            if (isGpt5OrNewer)
            {
                // ✅ Use new parameter name for GPT-5 models
                requestData = new
                {
                    model,
                    messages = inputMessages,
                    temperature = 1.0,
                    max_completion_tokens = 15000
                };
            }
            else
            {
                // ✅ Legacy GPT-4 and earlier models
                requestData = new
                {
                    model,
                    messages = inputMessages,
                    temperature = 1.0,
                    max_tokens = 15000
                };
            }

            var requestJson = JsonConvert.SerializeObject(requestData);
            var requestContent = new StringContent(requestJson, Encoding.UTF8, "application/json");

            try
            {
                var response = await _httpClient.PostAsync(
                    "https://api.openai.com/v1/chat/completions",
                    requestContent);

                var jsonResponse = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[GPT ERROR] {response.StatusCode}\n{jsonResponse}");
                    return new { assistantText = $"API Error: {response.StatusCode}", rawResponse = jsonResponse, error = true };
                }

                dynamic result = JsonConvert.DeserializeObject<dynamic>(jsonResponse);
                string aiResponse = result?.choices?[0]?.message?.content?.ToString();

                if (!string.IsNullOrWhiteSpace(aiResponse))
                {
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
                }

                if (string.IsNullOrWhiteSpace(aiResponse))
                    aiResponse = "⚠️ No response from GPT (empty content).";

                bool isComplete = aiResponse.Contains("==PLACEHOLDER_VALUES_START==") &&
                                  aiResponse.Contains("==PLACEHOLDER_VALUES_END==") &&
                                  aiResponse.Contains("\"complete\"");

                if (isComplete)
                {
                    ClearChatHistory(userId);
                    return new
                    {
                        isComplete = true,
                        assistantText = aiResponse,
                        fullResponse = result,
                        sessionActive = false,
                        messageCount = 0
                    };
                }

                if (_sessions.ContainsKey(userId))
                {
                    _sessions[userId].Messages.Add(new Dictionary<string, string>
            {
                { "role", "assistant" },
                { "content", aiResponse }
            });
                }

                return new
                {
                    isComplete = false,
                    assistantText = aiResponse,
                    fullResponse = result,
                    sessionActive = true,
                    messageCount = _sessions[userId].Messages.Count
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SEND_TO_GPT ERROR] {ex.Message}");
                return new { assistantText = $"⚠️ GPT request failed - {ex.Message}", error = true };
            }
        }

        // ✅ Parse placeholder markers
        private Dictionary<string, string> ExtractPlaceholders(string aiResponse)
        {
            var dict = new Dictionary<string, string>();
            var blockMatch = Regex.Match(aiResponse,
                "==PLACEHOLDER_VALUES_START==(.*?)==PLACEHOLDER_VALUES_END==",
                RegexOptions.Singleline);

            if (!blockMatch.Success) return dict;

            var block = blockMatch.Groups[1].Value;
            var lines = block.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                var kv = Regex.Match(line, @"\{([^}]+)\}\s*=\s*(.*)");
                if (kv.Success)
                {
                    dict[kv.Groups[1].Value.Trim()] = kv.Groups[2].Value.Trim();
                }
            }

            return dict;
        }

        // ✅ Save to DB using injected IServiceScopeFactory
        // ✅ Save placeholder values, build filled placeholder list and final campaign blueprint
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

            // 🧠 1️⃣ Merge placeholder values
            var existing = string.IsNullOrEmpty(campaign.PlaceholderValues)
                ? new Dictionary<string, string>()
                : JsonConvert.DeserializeObject<Dictionary<string, string>>(campaign.PlaceholderValues) ?? new();

            foreach (var kv in newValues)
                existing[kv.Key] = kv.Value;

            campaign.PlaceholderValues = JsonConvert.SerializeObject(existing);

            // 🏗️ 2️⃣ Build "placeholder list with values" (human readable)
            campaign.PlaceholderListWithValue = string.Join("\n", existing.Select(kv => $"{{{kv.Key}}} = {kv.Value}"));

            // 💌 3️⃣ Build filled campaign blueprint
            try
            {
                string unpopulated = campaign.TemplateDefinition?.MasterBlueprintUnpopulated ?? string.Empty;
                string filledBlueprint = unpopulated;

                foreach (var (key, value) in existing)
                {
                    if (string.IsNullOrWhiteSpace(key)) continue;
                    // case‑insensitive placeholder replacement
                    filledBlueprint = Regex.Replace(
                        filledBlueprint,
                        $"{{{Regex.Escape(key)}}}",
                        value ?? "",
                        RegexOptions.IgnoreCase);
                }

                campaign.CampaignBlueprint = filledBlueprint;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Blueprint Build Error] {ex.Message}");
            }

            // 🕒 4️⃣ Save time stamp and persist
            campaign.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            Console.WriteLine($"💾  Campaign {campaign.Id} updated: {existing.Count} placeholders saved, list + blueprint generated.");
        }

        public class CompletionResponse
        {
            [JsonProperty("status")]
            public string Status { get; set; } = string.Empty;
            [JsonProperty("final_prompt")]
            public string FinalPrompt { get; set; } = string.Empty;
        }

       
        // ✅ Generate example output — now fully compatible with GPT-4, GPT-4.1, GPT-5+
        public async Task<string?> GenerateExampleOutputAsync(
            Dictionary<string, string> placeholderValues,
            string masterTemplate,
            string model = "gpt-4.1")
        {
            if (placeholderValues == null || placeholderValues.Count == 0)
                return null;

            // 1️⃣ Fill placeholders
            string filledTemplate = masterTemplate;
            foreach (var (key, value) in placeholderValues)
                filledTemplate = filledTemplate.Replace($"{{{key}}}", value ?? "", StringComparison.OrdinalIgnoreCase);

            bool isGpt5 = model.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase);
            bool isGpt41 = model.StartsWith("gpt-4.1", StringComparison.OrdinalIgnoreCase);

            object requestData;
            string endpoint;

            if (isGpt5)
            {
                endpoint = "https://api.openai.com/v1/responses";
                requestData = new
                {
                    model = model,
                    input = filledTemplate,
                    reasoning = new { effort = "high" },
                    text = new { verbosity = "medium" }
                    
                    // max_output_tokens = 2000
                };
            }
            else if (isGpt41)
            {
                // ✅ GPT-4.1 also uses Responses API but keeps temperature
                endpoint = "https://api.openai.com/v1/responses";
                requestData = new
                {
                    model,
                    input = filledTemplate,
                    temperature = 1.0,
                    max_output_tokens = 20000
                };
            }
            else
            {
                // ✅ GPT-4 and earlier → Chat Completions API
                endpoint = "https://api.openai.com/v1/chat/completions";
                requestData = new
                {
                    model,
                    messages = new[]
                    {
                new { role = "system", content = "You are an assistant that formats marketing emails in HTML." },
                new { role = "user", content = filledTemplate }
            },
                    temperature = 1.0,
                    max_tokens = 20000
                };
            }

            var json = JsonConvert.SerializeObject(requestData);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            try
            {
                var response = await _httpClient.PostAsync(endpoint, content);
                var body = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[GenerateExampleOutputAsync] ❌ API Error {response.StatusCode}: {body}");
                    return null;
                }

                dynamic parsed = JsonConvert.DeserializeObject(body);
                string html = null;

                // ✅ Responses API (GPT-4.1 & GPT-5)
                html = parsed?.output_text?.ToString()
                    ?? parsed?.output?[0]?.content?[0]?.text?.ToString();

                // ✅ Chat Completions fallback (GPT-4)
                if (string.IsNullOrWhiteSpace(html))
                    html = parsed?.choices?[0]?.message?.content?.ToString();

                return string.IsNullOrWhiteSpace(html) ? null : html.Trim();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GenerateExampleOutputAsync] Exception: {ex.Message}");
                return null;
            }
        }

        public class WebSearchService
        {
            public class WebSearchResponse
            {
                [JsonProperty("output")]
                public List<OutputItem> Output { get; set; } = new();
            }

            public class OutputItem
            {
                [JsonProperty("content")] public List<OutputContent> Content { get; set; } = new();
                [JsonProperty("type")] public string Type { get; set; } = string.Empty;
                [JsonProperty("status")] public string Status { get; set; } = string.Empty;
                [JsonProperty("action")] public SearchAction Action { get; set; } = new();
            }

            public class OutputContent
            {
                [JsonProperty("type")] public string Type { get; set; } = string.Empty;
                [JsonProperty("text")] public string Text { get; set; } = string.Empty;
            }

            public class SearchAction
            {
                [JsonProperty("query")] public string Query { get; set; } = string.Empty;
                [JsonProperty("domains")] public List<string> Domains { get; set; } = new();
                [JsonProperty("sources")] public List<WebSource> Sources { get; set; } = new();
            }

            public class WebSource
            {
                [JsonProperty("url")] public string Url { get; set; } = string.Empty;
                [JsonProperty("title")] public string Title { get; set; } = string.Empty;
                [JsonProperty("snippet")] public string Snippet { get; set; } = string.Empty;
            }
        }
    }
}