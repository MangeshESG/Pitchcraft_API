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
        private readonly ContactRepository _contactRepository;
        private readonly string _apiKey;

        public ContactQAService(
            HttpClient httpClient,
            AppDbContext context,
            ContactRepository contactRepository,
            IOptions<OpenAISettings> openAIOptions)
        {
            _httpClient = httpClient;
            _context = context;
            _apiKey = openAIOptions.Value.ApiKey;
            _contactRepository = contactRepository;
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
                    2. Anything the CRM context does not answer is governed by the INTERNET RESEARCH POLICY below. Follow it exactly.
                    3. The user chooses which record sources this question may use. Follow the SELECTED SOURCES section below: never answer from a source that was excluded, and never treat an excluded source as proof that nothing exists.
                    4. Do NOT infer, guess, assume, or fill missing gaps.
                    5. If the answer is not supported by either CRM context or web results, say exactly:
                       No information found in the provided context.
                    6. A single answer may require combining evidence from multiple source records.
                       You MUST consider all relevant sources together, including:
                       - Contact profile fields
                       - LinkedIn summary
                       - LinkedIn messages (direction=outbound was sent by us, direction=inbound was sent by the contact)
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
                    10. For LinkedIn message answers, state who sent it (us or the contact) and when, and quote the part of the body that answers the question.
                    11. Never fabricate timestamps, identifiers, subjects, page titles, or source names.
                    12. Prefer exact source identifiers from the CRM context JSON, such as:
                        NOTE-1, EMAIL-2, EMAIL-2-REPLY-1, LINKEDIN-1, LIMSG-3
                    13. If only partial information exists, answer with only that partial information and explicitly state what is missing.
                    14. Whenever you show a date, format it as:
                        Month d, yyyy
                        Example: September 6, 2019
                    15. Do NOT use numeric date formats such as:
                        - ddmmyyyy
                        - dd/mm/yyyy
                        - yyyy-mm-dd

                    Source attribution format (MANDATORY):
                    [Source: <type> | <title/subject/identifier> | <Month d, yyyy or Not Timestamped>]

                    Examples:
                    [Source: Email | Intro call follow-up | September 6, 2019]
                    [Source: Note | NOTE-1 | Not Timestamped]
                    [Source: LinkedInMessage | LIMSG-3 (inbound) | May 4, 2026]
                    [Source: Web | Company funding announcement | May 4, 2026]

                    Where:
                    - <type> = Note / Email / LinkedIn / LinkedInMessage / Web / Attachment
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

            // Null means the caller predates the Internet research checkbox, so it
            // keeps the old always-on behaviour.
            var allowWebSearch = request.AllowWebSearch ?? true;

            var researchPolicy = allowWebSearch
                ? """
                    INTERNET RESEARCH POLICY: ENABLED (the user ticked Internet research).
                    - Answer from the CRM context first.
                    - If the CRM context does not answer the question, or the question asks for current/latest/recent/live/public information, you MAY use web search.
                    - Keep CRM-based facts and web-based facts clearly separated, and cite every web fact as [Source: Web | <page title> | <date>].
                    """
                : """
                    INTERNET RESEARCH POLICY: DISABLED (the user did not tick Internet research).
                    - You MUST NOT search the web, and you MUST NOT answer from outside knowledge about the person, the company, or the market.
                    - The provided CRM context and the prior chat are the only permitted sources.
                    - Never emit a [Source: Web | ...] citation.
                    - If the CRM context does not answer the question, output exactly: No information found in the provided context.
                      You may add one short line suggesting the user tick Internet research if live web data would answer it.
                    """;

            var sourcePolicy = DescribeSelectedSources(request.Sources);

            systemPrompt = string.Join(
                "\n\n",
                new[] { systemPrompt, researchPolicy, sourcePolicy }
                    .Where(section => !string.IsNullOrWhiteSpace(section)));

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
                { "max_output_tokens", rate.MaxTokens }
            };

            // No tool at all when research is off: a prompt rule alone still
            // leaves the model free to call it, and a search the user did not
            // ask for is both slower and billed.
            if (allowWebSearch)
            {
                requestData["tools"] = new object[]
                {
                    new
                    {
                        type = "web_search",
                        external_web_access = true
                    }
                };
                requestData["tool_choice"] = "auto";
                requestData["include"] = new[] { "web_search_call.action.sources" };
            }

            AiModelDefaults.ApplySampling(
                requestData, request.ModelName, (double)rate.Temperature);

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

            // A 200 can still be a truncated generation, and reasoning models burn the output
            // budget before writing any text. Fail before spending the client's credit.
            var incompleteReason = OpenAiResponseGuard.GetIncompleteReason(parsed);

            if (incompleteReason != null || string.IsNullOrWhiteSpace(output))
            {
                return new ContactQAResponse
                {
                    IsSuccess = false,
                    Answer = OpenAiResponseGuard.DescribeEmptyOutput(incompleteReason, rate.MaxTokens),
                    PromptTokens = promptTokens,
                    CompletionTokens = completionTokens,
                    TotalTokens = totalTokens,
                    CurrentCost = currentCost
                };
            }

            await _contactRepository.CreditDeduction(request.ClientId);

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

        /// <summary>
        /// Readable labels for the source keys the UI sends, so the prompt can
        /// name what the user excluded. An unknown key is still worth echoing —
        /// better a prettified key than silently dropping a source.
        /// </summary>
        private static readonly Dictionary<string, string> SourceLabels = new(StringComparer.OrdinalIgnoreCase)
        {
            ["profile"] = "Contact profile and custom fields",
            ["notes"] = "Internal notes",
            ["emails"] = "Emails, replies, and email activity",
            ["linkedinProfile"] = "LinkedIn profile info",
            ["linkedinMessages"] = "LinkedIn messages (both directions)",
            ["savedResearch"] = "Saved web research insights"
        };

        private static string? DescribeSelectedSources(Dictionary<string, bool>? sources)
        {
            if (sources == null || sources.Count == 0)
            {
                return null;
            }

            // Internet research has its own policy section; it is not a record source.
            var recordSources = sources
                .Where(entry => !string.Equals(entry.Key, "internet", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (recordSources.Count == 0)
            {
                return null;
            }

            var included = recordSources.Where(entry => entry.Value).Select(entry => Label(entry.Key)).ToList();
            var excluded = recordSources.Where(entry => !entry.Value).Select(entry => Label(entry.Key)).ToList();

            var builder = new StringBuilder();
            builder.AppendLine("SELECTED SOURCES (chosen by the user for this question):");
            builder.AppendLine(included.Count > 0
                ? $"- Included: {string.Join(", ", included)}"
                : "- Included: none");

            if (excluded.Count > 0)
            {
                builder.AppendLine($"- Excluded: {string.Join(", ", excluded)}");
                builder.AppendLine("The excluded sources were deliberately withheld and are not in the context JSON. Do not answer from them, do not guess at their contents, and do not claim they are empty. If the question depends on one, say which source is switched off.");
            }

            return builder.ToString().TrimEnd();

            static string Label(string key) =>
                SourceLabels.TryGetValue(key, out var label) ? label : key;
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
