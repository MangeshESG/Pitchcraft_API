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
        private readonly AppDbContext _context;
        private readonly string _apiKey;
        private readonly ContactRepository _contactRepository;
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
            AppDbContext context,
            ContactRepository contactRepository,
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
            _context = context;
            _contactRepository = contactRepository;

        }

        private static readonly HashSet<string> RuntimeOnlyPlaceholders =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "full_name",
            "first_name",
            "last_name",
            "job_title",
            "location",
            "linkedin_url",
            "company_name",
            "company_name_friendly",
            "website"
        };


        // Single method to handle both start and continue
        public async Task<object> ProcessChatAsync(string userId, string message, string systemPrompt, string model, string? imageUrl = null)
        {
            if (string.IsNullOrWhiteSpace(userId))
                return new { assistantText = "⚠️ UserId is required", error = true };

            // ------------------------------------------------------
            // 🟢 1. Create session if not exists
            // ------------------------------------------------------
            if (!_sessions.ContainsKey(userId))
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                // Load latest created campaign for this user
                var campaign = await db.CampaignTemplates
                    .Where(c => c.ClientId == userId)
                    .OrderByDescending(c => c.CreatedAt)
                    .Select(c => new
                    {
                        c.Id,
                        TemplateDefinition = db.CampaignTemplateDefinitions
                            .Where(t => t.Id == c.TemplateDefinitionId)
                            .Select(t => new
                            {
                                t.AIInstructions,
                                t.AIInstructionsForEdit,
                                t.PlaceholderList,
                                t.PlaceholderListExtensive,
                                t.MasterBlueprintUnpopulated
                            })
                            .FirstOrDefault()
                    })
                    .FirstOrDefaultAsync();


                if (campaign == null)
                {
                    return new
                    {
                        assistantText = "⚠️ No campaign found. Call /campaign/start first.",
                        error = true
                    };
                }

                // Build system prompt from DB (IGNORE frontend systemPrompt)
                string systemPromptToUse = campaign.TemplateDefinition?.AIInstructions ?? "";

                if (string.IsNullOrWhiteSpace(systemPromptToUse))
                {
                    systemPromptToUse = campaign.TemplateDefinition?.PlaceholderListExtensive
                                        ?? campaign.TemplateDefinition?.PlaceholderList
                                        ?? "";
                }

                // Initialize session
                _sessions[userId] = new CampaignSession
                {
                    UserId = userId,
                    CampaignTemplateId = campaign.Id,
                    Messages = new List<Dictionary<string, string>>
            {
                new()
                {
                    { "role", "system" },
                    { "content", systemPromptToUse }
                }
            }
                };
            }

            // ------------------------------------------------------
            // 🟢 2. Attach campaign ID if missing (rare case)
            // ------------------------------------------------------
            var session = _sessions[userId];

            if (session.CampaignTemplateId == 0)
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                var lastCampaign = await db.CampaignTemplates
                    .OrderByDescending(c => c.CreatedAt)
                    .FirstOrDefaultAsync(c => c.ClientId == userId);

                if (lastCampaign != null)
                {
                    session.CampaignTemplateId = lastCampaign.Id;
                }
                else
                {
                    return new { assistantText = "⚠️ No campaign found. Start one first.", error = true };
                }
            }

            // ------------------------------------------------------
            // 🟢 3. Validate message
            // ------------------------------------------------------
            if (string.IsNullOrWhiteSpace(message))
                return new { assistantText = "⚠️ Message is required.", error = true };

            // ------------------------------------------------------
            // 🟢 4. Append user message
            // ------------------------------------------------------
            session.Messages.Add(new Dictionary<string, string>
    {
        { "role", "user" },
        { "content", message }
    });

            // ------------------------------------------------------
            // 🟢 5. Call GPT
            // ------------------------------------------------------
            var response = await SendToGptAsync(session.Messages, model, userId, imageUrl);

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
        private async Task<object> SendToGptAsync(List<Dictionary<string, string>> messages, string model, string userId, string? imageUrl = null)
        {
            if (string.IsNullOrWhiteSpace(model))
                model = "gpt-5.1";

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

            if (!string.IsNullOrWhiteSpace(imageUrl))
            {
                if (!Regex.IsMatch(imageUrl, @"\.(png|jpg|jpeg)$", RegexOptions.IgnoreCase))
                {
                    return new
                    {
                        assistantText = "Only PNG, JPG, or JPEG images are supported.",
                        error = true
                    };
                }
            }

            // --------------------------------------
            // 🔹 Build INPUT payload (TEXT or TEXT+IMAGE)
            // --------------------------------------
            object inputPayload;

                                    if (!string.IsNullOrWhiteSpace(imageUrl))
                                    {
                                        // ✅ IMAGE + TEXT (Vision enabled)
                                        inputPayload = new object[]
                                        {
                                new
                                {
                                    role = "user",
                                    content = new object[]
                                    {
                                    new
                                    {
                                        type = "input_text",
                                        text = messages.Last(m => m["role"] == "user")["content"]
                                    },

                                        new
                                        {
                                            type = "input_image",
                                            image_url = imageUrl
                                        }
                                    }
                                }
                                        };
                                    }
                                    else
                                    {
                                        // ✅ TEXT ONLY (existing behavior)
                                        inputPayload = sbInput.ToString();
            }

            var requestBody = new Dictionary<string, object>
                        {
                            { "model", model },
                            { "input", inputPayload },
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

                int clientId = int.Parse(userId);
                await _contactRepository.CreditDeduction(clientId);

                if (!httpResponse.IsSuccessStatusCode)
                {
                    return new
                    {
                        assistantText = $"API Error: {httpResponse.StatusCode}",
                        rawResponse = raw,
                        error = true
                    };
                }

                var parsed = JsonConvert.DeserializeObject<JObject>(raw)!;

                // Prefer output_text
                string aiResponse = parsed["output_text"]?.ToString() ?? string.Empty;

                if (string.IsNullOrWhiteSpace(aiResponse))
                {
                    aiResponse = ExtractTextFromOutputs(parsed);
                }

                if (string.IsNullOrWhiteSpace(aiResponse))
                    aiResponse = "⚠️ No response from GPT (empty content).";


                // ============================================================
                // 🟢 NEW — USAGE EXTRACTION
                // ============================================================
                int promptTokens = parsed["usage"]?["input_tokens"]?.Value<int>() ?? 0;
                int completionTokens = parsed["usage"]?["output_tokens"]?.Value<int>() ?? 0;
                int searchTokens = parsed["usage"]?["search_tokens"]?.Value<int>() ?? 0;

                // search_calls always 1 when tool is enabled
                int searchCalls = 1;


                // ============================================================
                // 🟢 NEW — LOAD MODEL PRICING
                // ============================================================
                var rate = await _context.ModelRates
                    .FirstOrDefaultAsync(x => x.ModelName == model);

                if (rate == null)
                    rate = await _context.ModelRates.FirstAsync(x => x.ModelName == "gpt-5");

                decimal inputPrice = rate.InputPrice;     // per 1M
                decimal outputPrice = rate.OutputPrice;   // per 1M


                // ============================================================
                // 🟢 NEW — COST CALCULATION
                // ============================================================
                decimal cost =
                    (promptTokens * inputPrice / 1_000_000m) +
                    (completionTokens * outputPrice / 1_000_000m) +
                    (searchTokens * inputPrice / 1_000_000m) +
                    0.01m; // web search fee


                // ============================================================
                // Save placeholders (existing logic)
                // ============================================================
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
                    _sessions[userId] = new CampaignSession
                    {
                        UserId = userId,
                        CampaignTemplateId = 0,
                        Messages = new List<Dictionary<string, string>>()
                    };
                }

                _sessions[userId].Messages.Add(new Dictionary<string, string>
        {
            { "role", "assistant" },
            { "content", aiResponse }
        });


                // ============================================================
                // Collect web search sources (existing logic)
                // ============================================================
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


                // ============================================================
                // ⭐ FINAL RETURN WITH USAGE
                // ============================================================
                return new
                {
                    isComplete = aiResponse.Contains("==PLACEHOLDER_VALUES_START==") &&
                                 aiResponse.Contains("==PLACEHOLDER_VALUES_END==") &&
                                 aiResponse.Contains("\"complete\""),

                    assistantText = aiResponse,
                    fullResponse = parsed,
                    sessionActive = true,
                    messageCount = _sessions[userId].Messages.Count,
                    webSearchResults,

                    usage = new
                    {
                        promptTokens,
                        completionTokens,
                        searchTokens,
                        searchCalls,
                        totalTokens = promptTokens + completionTokens + searchTokens,
                        cost
                    }
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

            // -------------------------------
            // Deserialize existing values
            // -------------------------------
            var existing = string.IsNullOrEmpty(campaign.PlaceholderValues)
                ? new Dictionary<string, string>()
                : JsonConvert.DeserializeObject<Dictionary<string, string>>(campaign.PlaceholderValues)
                  ?? new Dictionary<string, string>();

            // -------------------------------
            // Remove runtime-only placeholders
            // -------------------------------
            var filtered = RemoveRuntimeOnlyPlaceholders(newValues);

            foreach (var kv in filtered)
                existing[kv.Key] = kv.Value;

            // -------------------------------
            // ✅ SINGLE DB UPDATE (NO LOOP)
            // -------------------------------
            campaign.PlaceholderValues = JsonConvert.SerializeObject(existing);
            campaign.PlaceholderListWithValue =
                string.Join("\n", existing.Select(kv => $"{{{kv.Key}}} = {kv.Value}"));

            // ❌ NEVER touch CampaignBlueprint here
            campaign.UpdatedAt = DateTime.UtcNow;

            await db.SaveChangesAsync();
        }

        private static string ApplyPlaceholders(
            string? blueprint,
            Dictionary<string, string>? values)
        {
            if (string.IsNullOrWhiteSpace(blueprint) || values == null || values.Count == 0)
                return blueprint ?? string.Empty;

            string result = blueprint;

            foreach (var (key, value) in values)
            {
                result = Regex.Replace(
                    result,
                    $"{{{Regex.Escape(key)}}}",
                    value ?? string.Empty,
                    RegexOptions.IgnoreCase
                );
            }

            return result;
        }

        private static Dictionary<string, string> RemoveRuntimeOnlyPlaceholders(
        Dictionary<string, string>? input)
        {
            if (input == null || input.Count == 0)
                return new Dictionary<string, string>();

            return input
                .Where(kv => !RuntimeOnlyPlaceholders.Contains(kv.Key))
                .ToDictionary(
                    kv => kv.Key,
                    kv => kv.Value,
                    StringComparer.OrdinalIgnoreCase
                );
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
            string model = "gpt-5.1")
        {
            if (placeholderValues == null || placeholderValues.Count == 0)
                return null;

            // ⭐ NEW — store filled template
            string lastFilledTemplate = "";

            // 1) Fill placeholders
            string filledTemplate = masterTemplate ?? string.Empty;
            foreach (var (key, value) in placeholderValues)
                filledTemplate = filledTemplate.Replace($"{{{key}}}", value ?? string.Empty, StringComparison.OrdinalIgnoreCase);

            // ⭐ NEW — capture filled template
            lastFilledTemplate = filledTemplate;

            // 2) Build role-prefixed input
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
            { "temperature", 0.1 },
            { "max_output_tokens", 15000 }
        };

                if (useTools)
                {
                    requestData["tools"] = new object[] { new { type = "web_search_preview" } };
                    requestData["text"] = new { verbosity = "low" };
                }

                var json = JsonConvert.SerializeObject(requestData);
                var httpContent = new StringContent(json, Encoding.UTF8, "application/json");

                try
                {
                    var response = await _httpClient.PostAsync("https://api.openai.com/v1/responses", httpContent);
                    var raw = await response.Content.ReadAsStringAsync();

                    if (!response.IsSuccessStatusCode)
                    {
                        if (useTools && raw.IndexOf("Unsupported parameter", StringComparison.OrdinalIgnoreCase) >= 0)
                            continue;

                        if (!useTools) return null;
                        continue;
                    }

                    var parsed = JsonConvert.DeserializeObject<JObject>(raw)!;
                    string resultText = parsed["output_text"]?.ToString() ?? string.Empty;

                    if (string.IsNullOrWhiteSpace(resultText))
                        resultText = ExtractTextFromOutputs(parsed);

                    if (!string.IsNullOrWhiteSpace(resultText))
                    {
                        // ⭐ NEW — return filled template + HTML in single string
                        return $"__FILLED_TEMPLATE_START__{lastFilledTemplate}__FILLED_TEMPLATE_END__{resultText.Trim()}";
                    }

                    if (useTools) continue;
                    return null;
                }
                catch
                {
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
            return await SendToGptAsync(_sessions[req.UserId].Messages, req.Model ?? "gpt-5.1", req.UserId, req.ImageUrl);
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
            return await SendToGptAsync(session.Messages, req.Model ?? "gpt-5.1", req.UserId, req.ImageUrl);
        }

        public async Task<CampaignTemplate?> RenameTemplate(RenameTemplate rename)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // Fetch template
            var template = await db.CampaignTemplates
                .FirstOrDefaultAsync(t => t.ClientId == rename.clientId && t.Id == rename.templateId);

            if (template == null)
                return null;

            // Update template name
            template.TemplateName = rename.TemplateName;
            template.UpdatedAt = DateTime.UtcNow;

            await db.SaveChangesAsync();

            return template;
        }
        public async Task<CampaignTemplate?> CloneTemplateAsync(string clientId, int templateId,string Name)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // Fetch the original template
            var template = await db.CampaignTemplates
                .FirstOrDefaultAsync(t => t.ClientId == clientId && t.Id == templateId);

            if (template == null)
                return null;

            // Create a new template (clone)
            var clonedTemplate = new CampaignTemplate
            {
                ClientId = template.ClientId,
                TemplateDefinitionId = template.TemplateDefinitionId,
                PlaceholderListWithValue = template.PlaceholderListWithValue,
                CampaignBlueprint = template.CampaignBlueprint,
                PlaceholderValues = template.PlaceholderValues,
                SelectedModel = template.SelectedModel,
                TemplateName = Name,
                ExampleOutput = template.ExampleOutput,
                SearchURLCount = template.SearchURLCount,
                SubjectInstructions = template.SubjectInstructions,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            db.CampaignTemplates.Add(clonedTemplate);
            await db.SaveChangesAsync();

            return clonedTemplate;
        }

    }

}
