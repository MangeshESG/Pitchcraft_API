namespace PitchGenApi.Services
{
    using System.Text;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Options;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;
    using PitchGenApi.Database;
    using PitchGenApi.Interfaces;
    using PitchGenApi.Model;
    using PitchGenApi.Model.DTOs;

    public class ContactQAService : IContactQAService
    {
        private readonly HttpClient _httpClient;
        private readonly AppDbContext _context;
        private readonly string _apiKey;

        public ContactQAService(
            HttpClient httpClient,
            AppDbContext context,
            IOptions<OpenAISettings> openAIOptions)
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
            {
                return new ContactQAResponse
                {
                    IsSuccess = false,
                    Answer = "Question is required."
                };
            }

            if (request.ClientId <= 0)
            {
                return new ContactQAResponse
                {
                    IsSuccess = false,
                    Answer = "Valid ClientId is required."
                };
            }

            var rate = await _context.ModelRates.FirstOrDefaultAsync(m => m.ModelName == request.ModelName)
                       ?? await _context.ModelRates.FirstOrDefaultAsync(m => m.ModelName == "gpt-5.1");

            if (rate == null)
            {
                return new ContactQAResponse
                {
                    IsSuccess = false,
                    Answer = "Model pricing not configured."
                };
            }

            var systemPrompt = """
You are a contact intelligence assistant for CRM prospect Q&A.

You will receive:
- prior chat messages
- one structured prospect context JSON block

That JSON is the primary source of truth.

Rules:
1. First, answer from the provided CRM context and prior chat.
2. If the CRM context does not contain the answer, or the user asks for current/latest/recent/live/public web information, you MAY use web search.
3. When web search is used, clearly separate CRM-based facts from web-based facts.
4. Do NOT infer, guess, assume, or fill missing gaps.
5. If the answer is not supported by either CRM context or web results, say exactly:
   No information found in the provided context.
6. A single answer may require combining evidence from multiple source records.
   You MUST consider all relevant sources together, including:
   - Contact profile fields
   - LinkedIn summary
   - Notes
   - Emails
   - Email replies
   - Email activity/events
   - Web search results
7. If multiple sources support the answer, include ALL relevant sources.
8. If sources conflict, state the conflict clearly and cite each conflicting source.
9. For email-related answers, include when available:
   - subject
   - sender
   - receiver
   - key question, intent, or message from the email body
10. Never fabricate timestamps, identifiers, subjects, page titles, or source names.
11. Prefer exact source identifiers from the CRM context JSON, such as:
    NOTE-1, EMAIL-2, EMAIL-2-REPLY-1, LINKEDIN-1
12. If only partial information exists, answer with only that partial information and explicitly state what is missing.
13. Whenever you show a date, format it as:
    Month d, yyyy
    Example: September 6, 2019
14. Do NOT use numeric date formats such as:
    - ddmmyyyy
    - dd/mm/yyyy
    - yyyy-mm-dd

Source attribution format (MANDATORY):
[Source: <type> | <title/subject/identifier> | <Month d, yyyy or Not Timestamped>]

Examples:
[Source: Email | Intro call follow-up | September 6, 2019]
[Source: Note | NOTE-1 | Not Timestamped]
[Source: Web | Company funding announcement | May 4, 2026]

Where:
- <type> = Note / Email / LinkedIn / Web / Attachment
- <title/subject/identifier> = exact sourceId, noteId, subject, profile identifier, or web page title
- <Month d, yyyy or Not Timestamped> = exact date if available, otherwise Not Timestamped

UI formatting rules:
1. Keep the response compact and clean.
2. Do NOT use markdown headings like # or ##.
3. Do NOT use code blocks.
4. Do NOT use tables.
5. Do NOT insert unnecessary blank lines.
6. Use at most one empty line between Answer and Sources.
7. In Sources, each source must be on a single bullet line.
8. If the answer is short, keep it to 1-3 short sentences.

Output format:

Answer: <compact factual answer>

Sources:
- [Source: ...]
- [Source: ...]

If no answer exists, output exactly:

Answer: No information found in the provided context.

Sources:
- [Source: None]
""";

            var contextPayload = request.ContextSummary;
            if (string.IsNullOrWhiteSpace(contextPayload))
            {
                contextPayload = JsonConvert.SerializeObject(request.Context, Formatting.Indented);
            }

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
                        new { type = "input_text", text = contextPayload }
                    }
                }
            };

            foreach (var message in request.Messages ?? new List<ContactQAMessageDto>())
            {
                if (string.IsNullOrWhiteSpace(message.Content))
                {
                    continue;
                }

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
                { "max_output_tokens", rate.MaxTokens },
                {
                    "tools",
                    new object[]
                    {
                        new
                        {
                            type = "web_search",
                            external_web_access = true
                        }
                    }
                },
                { "tool_choice", "auto" },
                { "include", new[] { "web_search_call.action.sources" } }
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
            var output = parsed["output_text"]?.ToString() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(output))
            {
                var outputs = parsed["output"] as JArray;
                if (outputs != null)
                {
                    var sb = new StringBuilder();

                    foreach (var item in outputs)
                    {
                        var contentArray = item["content"] as JArray;
                        if (contentArray == null)
                        {
                            continue;
                        }

                        foreach (var c in contentArray)
                        {
                            var text = c["text"]?.ToString();
                            if (!string.IsNullOrWhiteSpace(text))
                            {
                                sb.AppendLine(text.Trim());
                            }
                        }
                    }

                    output = sb.ToString().Trim();
                }
            }

            output = NormalizeAssistantReply(output);

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

        private static string NormalizeAssistantReply(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            return value
                .Replace("\r\n", "\n")
                .Replace("\r", "\n")
                .Replace("\n\n\n", "\n\n")
                .Trim();
        }
    }
}
