using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PitchGenApi.Database;
using PitchGenApi.Model;

namespace PitchGenApi.Services
{
    public class DeepSeekPitchService
    {
        // Server-side web search lives on DeepSeek's Responses API, not on
        // /chat/completions (whose tools array only takes caller-run functions).
        private const string WebSearchToolType = "web_search";

        // Whether a given DeepSeek model actually runs this tool is not
        // something to hard-code, and not something their docs settle. The
        // Responses compatibility table lists web_search among built-in tools
        // that are "Ignored"; other sections of the same guide describe it as
        // supported and server-side. Measured 2026-09-10, deepseek-v4-pro ran
        // searches and both flash names ran none — but a model alias can change
        // that overnight, and DeepSeek's changelog routes deepseek-v4-pro to
        // V4.1-Flash after 2026-09-14.
        //
        // So capability is never inferred from the model name here. The request
        // asks for search, and the caller checks WebSearchCalls on the way out
        // to find out whether it happened. A model that ignores the tool returns
        // HTTP 200 with zero web_search_call items and an answer written from
        // memory, which is indistinguishable from a real answer until counted.

        private readonly HttpClient _httpClient;
        private readonly AppDbContext _context;
        private readonly ContactRepository _contactRepository;
        private readonly string _apiKey;
        private readonly string _baseUrl;

        public DeepSeekPitchService(
            HttpClient httpClient,
            AppDbContext context,
            ContactRepository contactRepository,
            IOptions<DeepSeekSettings> options)
        {
            _httpClient = httpClient;
            _context = context;
            _contactRepository = contactRepository;
            _apiKey = options.Value.ApiKey;
            _baseUrl = string.IsNullOrWhiteSpace(options.Value.BaseUrl)
                ? "https://api.deepseek.com"
                : options.Value.BaseUrl.TrimEnd('/');

            _httpClient.Timeout = TimeSpan.FromMinutes(3);

            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");
            _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
        }

        /// <summary>
        /// Flattens the exception chain into one line. A TLS failure surfaces as
        /// HttpRequestException("The SSL connection could not be established, see
        /// inner exception") - the sentence that names the actual cause (an
        /// untrusted certificate, a cipher mismatch, a closed transport) lives in
        /// the inner exception, so reporting only ex.Message throws it away.
        /// </summary>
        private static string Describe(Exception ex)
        {
            var parts = new List<string>();

            for (Exception? current = ex; current is not null; current = current.InnerException)
            {
                parts.Add($"{current.GetType().Name}: {current.Message}");
            }

            return string.Join(" -> ", parts);
        }

        public async Task<PitchResult> GeneratePitchAsync(EnquiryRequest request)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request.Prompt))
                {
                    return new PitchResult
                    {
                        Content = "Prompt is required.",
                        IsSuccess = false
                    };
                }

                if (string.IsNullOrWhiteSpace(request.ModelName))
                {
                    return new PitchResult
                    {
                        Content = "Model name is required.",
                        IsSuccess = false
                    };
                }

                string requestedModelName = request.ModelName.Trim();

                bool thinkingEnabled = requestedModelName.EndsWith(
                    "-thinking",
                    StringComparison.OrdinalIgnoreCase
                );

                string apiModelName = thinkingEnabled
                    ? requestedModelName.Replace("-thinking", "", StringComparison.OrdinalIgnoreCase)
                    : requestedModelName;

                var rate =
                    await _context.ModelRates.FirstOrDefaultAsync(m => m.ModelName == requestedModelName)
                    ?? await _context.ModelRates.FirstOrDefaultAsync(m => m.ModelName == apiModelName);

                decimal inputPricePerMillion = rate?.InputPrice ?? 0.27m;
                decimal outputPricePerMillion = rate?.OutputPrice ?? 1.10m;
                double temperature = Convert.ToDouble(rate?.Temperature ?? 0.7m);

                // The caller's budget wins: the rate row is sized for one email,
                // which is far too small for a batched JSON reply.
                int maxTokens = request.MaxTokens ?? rate?.MaxTokens ?? 2000;

                var messages = new List<object>();

                if (!string.IsNullOrWhiteSpace(request.ScrappedData))
                {
                    messages.Add(new
                    {
                        role = "system",
                        content = request.ScrappedData
                    });
                }

                messages.Add(new
                {
                    role = "user",
                    content = request.Prompt
                });

                bool isDeepSeekV4 = apiModelName.StartsWith(
                    "deepseek-v4-",
                    StringComparison.OrdinalIgnoreCase
                );

                var requestBody = new Dictionary<string, object>
                {
                    { "model", apiModelName },
                    { "messages", messages },
                    { "max_tokens", maxTokens },
                    { "stream", false }
                };

                if (isDeepSeekV4)
                {
                    requestBody["thinking"] = new
                    {
                        type = thinkingEnabled ? "enabled" : "disabled"
                    };

                    if (thinkingEnabled)
                    {
                        requestBody["reasoning_effort"] = "high";
                    }
                    else
                    {
                        requestBody["temperature"] = temperature;
                    }
                }
                else
                {
                    requestBody["temperature"] = temperature;
                }

                var json = JsonConvert.SerializeObject(requestBody);

                using var httpContent = new StringContent(
                    json,
                    Encoding.UTF8,
                    "application/json"
                );

                var response = await _httpClient.PostAsync(
                    $"{_baseUrl}/chat/completions",
                    httpContent
                );

                var responseContent = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    return new PitchResult
                    {
                        Content = $"DeepSeek API Error ({(int)response.StatusCode}): {responseContent}",
                        IsSuccess = false
                    };
                }

                var parsed = JObject.Parse(responseContent);

                string output =
                    parsed["choices"]?[0]?["message"]?["content"]?.ToString() ?? "";

                // A 200 can still be a generation that ran out of budget, and
                // DeepSeek says so only in finish_reason. Returning that as a
                // success hands the caller a half-written reply — JSON cut off
                // mid-array — which then fails somewhere far less obvious.
                string finishReason =
                    parsed["choices"]?[0]?["finish_reason"]?.ToString() ?? "";

                if (string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase))
                {
                    return new PitchResult
                    {
                        Content = $"Response truncated: hit max_tokens ({maxTokens}). "
                                + "Raise MaxTokens for this model in ModelRates, or ask for less in one call.",
                        IsSuccess = false,
                        PromptTokens = parsed["usage"]?["prompt_tokens"]?.Value<int>() ?? 0,
                        CompletionTokens = parsed["usage"]?["completion_tokens"]?.Value<int>() ?? 0,
                        CachedTokens = parsed["usage"]?["prompt_cache_hit_tokens"]?.Value<int>() ?? 0
                    };
                }

                if (string.IsNullOrWhiteSpace(output))
                {
                    return new PitchResult
                    {
                        Content = string.IsNullOrWhiteSpace(finishReason)
                            ? "The model returned no content."
                            : $"The model returned no content (finish_reason: {finishReason}).",
                        IsSuccess = false,
                        PromptTokens = parsed["usage"]?["prompt_tokens"]?.Value<int>() ?? 0,
                        CompletionTokens = parsed["usage"]?["completion_tokens"]?.Value<int>() ?? 0,
                        CachedTokens = parsed["usage"]?["prompt_cache_hit_tokens"]?.Value<int>() ?? 0
                    };
                }

                int promptTokens =
                    parsed["usage"]?["prompt_tokens"]?.Value<int>() ?? 0;

                int completionTokens =
                    parsed["usage"]?["completion_tokens"]?.Value<int>() ?? 0;

                int totalTokens =
                    parsed["usage"]?["total_tokens"]?.Value<int>()
                    ?? promptTokens + completionTokens;

                decimal currentCost =
                    (promptTokens * inputPricePerMillion / 1_000_000m) +
                    (completionTokens * outputPricePerMillion / 1_000_000m);

                return new PitchResult
                {
                    Content = output,
                    PromptTokens = promptTokens,
                    CompletionTokens = completionTokens,
                    TotalTokens = totalTokens,
                    CachedTokens = parsed["usage"]?["prompt_cache_hit_tokens"]?.Value<int>() ?? 0,
                    CurrentCost = currentCost,
                    IsSuccess = true
                };
            }
            catch (TaskCanceledException ex)
            {
                return new PitchResult
                {
                    Content = $"DeepSeek request timed out after {_httpClient.Timeout.TotalSeconds} seconds: {ex.Message}",
                    IsSuccess = false
                };
            }
            catch (Exception ex)
            {
                return new PitchResult
                {
                    Content = $"DeepSeek request failed: {Describe(ex)}",
                    IsSuccess = false
                };
            }
        }

        /// <summary>
        /// Runs the research step (the one that fills {web_searched_data}) on DeepSeek.
        /// DeepSeek's /chat/completions endpoint has no built-in search — its `tools`
        /// array only takes caller-executed functions — so this goes through the
        /// Responses API, which serves the same server-side web_search tool the
        /// OpenAI path uses. Pass clientId 0 to skip the credit deduction.
        /// </summary>
        public async Task<PitchResult> GenerateWebSearchAsync(EnquiryRequest request, int clientId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request?.Prompt))
                {
                    return new PitchResult
                    {
                        Content = "Prompt is required.",
                        IsSuccess = false
                    };
                }

                string requestedModelName = (request.ModelName ?? "").Trim();

                if (requestedModelName.Length == 0)
                {
                    return new PitchResult
                    {
                        Content = "Model name is required.",
                        IsSuccess = false
                    };
                }

                string apiModelName = requestedModelName.EndsWith(
                    "-thinking",
                    StringComparison.OrdinalIgnoreCase)
                    ? requestedModelName.Replace("-thinking", "", StringComparison.OrdinalIgnoreCase)
                    : requestedModelName;

                var rate =
                    await _context.ModelRates.FirstOrDefaultAsync(m => m.ModelName == requestedModelName)
                    ?? await _context.ModelRates.FirstOrDefaultAsync(m => m.ModelName == apiModelName);

                decimal inputPricePerMillion = rate?.InputPrice ?? 0.27m;
                decimal outputPricePerMillion = rate?.OutputPrice ?? 1.10m;
                int maxTokens = request.MaxTokens ?? rate?.MaxTokens ?? 2000;

                // DeepSeek documents `instructions` for system context and `input` for
                // the request itself. Sending the search instructions as a plain
                // string `input` matches their own examples exactly — the OpenAI-style
                // role/content array is a compatibility path we have not verified here.
                var requestBody = new Dictionary<string, object>
                {
                    { "model", apiModelName },
                    { "input", request.Prompt },
                    { "max_output_tokens", maxTokens },
                    {
                        "tools", new object[]
                        {
                            new { type = WebSearchToolType }
                        }
                    },
                    // Offering the tool only permits a search; tool_choice is
                    // what asks for one. Research callers want evidence, not a
                    // model deciding it already knows the answer.
                    //
                    // DeepSeek documents tool_choice as none/auto/required or a
                    // specific *function* tool, so naming a built-in this way may
                    // not be honoured. It is sent anyway because their stated
                    // rule is that unsupported parameters are silently ignored
                    // rather than rejected: if it works the search is forced, and
                    // if it does not this costs nothing. Either way the
                    // WebSearchCalls count, not this field, is what proves a
                    // search ran.
                    { "tool_choice", new { type = WebSearchToolType } }
                };

                if (!string.IsNullOrWhiteSpace(request.ScrappedData))
                {
                    requestBody["instructions"] = request.ScrappedData;
                }

                using var httpContent = new StringContent(
                    JsonConvert.SerializeObject(requestBody),
                    Encoding.UTF8,
                    "application/json"
                );

                var response = await _httpClient.PostAsync(
                    $"{_baseUrl}/v1/responses",
                    httpContent
                );

              var responseContent = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    // A 400 here is usually the configured model not serving the
                    // Responses API at all, or rejecting tool_choice on a
                    // built-in tool. Name the model so the reader can check it
                    // against DeepSeek's current model list rather than guessing.
                    return new PitchResult
                    {
                        Content = $"DeepSeek web search error ({(int)response.StatusCode}) "
                                + $"on model '{apiModelName}': {responseContent}",
                        IsSuccess = false
                    };
                }

                var parsed = JObject.Parse(responseContent);

                string output = parsed["output_text"]?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(output))
                    output = ExtractResponsesText(parsed);

                int promptTokens = parsed["usage"]?["input_tokens"]?.Value<int>() ?? 0;
                int completionTokens = parsed["usage"]?["output_tokens"]?.Value<int>() ?? 0;
                int totalTokens =
                    parsed["usage"]?["total_tokens"]?.Value<int>()
                    ?? promptTokens + completionTokens;

                int cachedTokens =
                    parsed.SelectToken("usage.input_tokens_details.cached_tokens")?.Value<int>()
                    ?? parsed.SelectToken("usage.prompt_cache_hit_tokens")?.Value<int>()
                    ?? 0;

                // DeepSeek bills web search as the extra model tokens it consumes,
                // so there is no separate per-search charge to add here.
                decimal currentCost =
                    (promptTokens * inputPricePerMillion / 1_000_000m) +
                    (completionTokens * outputPricePerMillion / 1_000_000m);

                // A run that spends its whole output budget researching comes
                // back status "incomplete" — or simply with the search calls and
                // no closing answer — and the tokens are spent either way. Say so
                // here: left to the caller it reads as a model that answered in
                // the wrong format, which sends the next person to fix the parser
                // rather than the budget.
                string status = parsed["status"]?.ToString() ?? "";

                // SelectToken, not parsed["incomplete_details"]?["reason"]: the
                // field is present and JSON null on every complete response, and
                // a JValue holding null is not a C# null, so ?. does not short
                // circuit and indexing into it throws InvalidOperationException.
                string incompleteReason =
                    parsed.SelectToken("incomplete_details.reason")?.ToString() ?? "";

                bool ranOut =
                    status.Equals("incomplete", StringComparison.OrdinalIgnoreCase) ||
                    incompleteReason.Length > 0;

                if (ranOut || string.IsNullOrWhiteSpace(output))
                {
                    string reason = ranOut
                        ? $"DeepSeek stopped before answering (status '{status}'"
                          + (incompleteReason.Length > 0 ? $", reason '{incompleteReason}'" : "")
                          + $"): the {maxTokens:N0} token output budget was spent on reasoning and "
                          + $"{CountWebSearchCalls(parsed)} web searches. Raise max_output_tokens or "
                          + "reduce the batch size."
                        : $"DeepSeek returned no answer text after {CountWebSearchCalls(parsed)} web "
                          + $"searches and {completionTokens:N0} output tokens against a "
                          + $"{maxTokens:N0} token budget.";

                    // Tokens and cost are still reported: the caller adds them to
                    // the job before it checks IsSuccess, so a failed batch is
                    // billed as accurately as a successful one.
                    return new PitchResult
                    {
                        Content = reason,
                        PromptTokens = promptTokens,
                        CompletionTokens = completionTokens,
                        TotalTokens = totalTokens,
                        CachedTokens = cachedTokens,
                        WebSearchCalls = CountWebSearchCalls(parsed),
                        SearchEvidence = ExtractSearchEvidence(parsed),
                        OutputItemTypes = ExtractOutputItemTypes(parsed),
                        CurrentCost = currentCost,
                        IsSuccess = false
                    };
                }

                if (clientId > 0)
                {
                    await _contactRepository.CreditDeduction(clientId);
                }

                return new PitchResult
                {
                    Content = output,
                    PromptTokens = promptTokens,
                    CompletionTokens = completionTokens,
                    TotalTokens = totalTokens,
                    CachedTokens = cachedTokens,
                    WebSearchCalls = CountWebSearchCalls(parsed),
                    SearchEvidence = ExtractSearchEvidence(parsed),
                    OutputItemTypes = ExtractOutputItemTypes(parsed),
                    CurrentCost = currentCost,
                    IsSuccess = true
                };
            }
            catch (TaskCanceledException ex)
            {
                return new PitchResult
                {
                    Content = $"DeepSeek web search timed out after {_httpClient.Timeout.TotalSeconds} seconds: {ex.Message}",
                    IsSuccess = false
                };
            }
            catch (Exception ex)
            {
                return new PitchResult
                {
                    Content = $"DeepSeek web search failed: {Describe(ex)}",
                    IsSuccess = false
                };
            }
        }

        /// <summary>
        /// How many server-side web searches the model actually ran, counted from
        /// the Responses output items.
        ///
        /// This is the number that decides what a research request costs — search
        /// is charged per call, and one request may make none or ten. Counting it
        /// per response is the only way to attribute the bill afterwards.
        /// </summary>
        public static int CountWebSearchCalls(JObject parsed)
        {
            if (parsed["output"] is not JArray outputs) return 0;

            // Contains, not an exact match on "web_search_call": if DeepSeek
            // ever labels the item differently — a suffixed or versioned type,
            // say — an exact comparison silently reports zero searches, which
            // now fails a batch. Reading a variant as a search is the safer
            // error of the two, and the OpenAI-side counter already matches
            // this way.
            return outputs.Count(item =>
                item["type"]?.ToString()?.Contains(WebSearchToolType, StringComparison.OrdinalIgnoreCase) == true);
        }

        /// <summary>
        /// The queries the model ran and the pages it opened, read from the
        /// action on each web_search_call item.
        ///
        /// A score without its evidence cannot be defended or re-checked later:
        /// "contact fit 82, four searches" says nothing about what was read, and
        /// the searching is the expensive half of producing it. DeepSeek
        /// documents the action shapes as search / open_page / find_in_page;
        /// anything unrecognised is kept by type rather than dropped, because an
        /// unfamiliar action still records that something was consulted.
        /// </summary>
        /// <summary>
        /// Every distinct item type the response carried, in order.
        ///
        /// Reported so that "no search happened" can be told apart from "the
        /// search was labelled something we do not recognise". Without it a
        /// zero-search result is an accusation against the model that may
        /// actually be a parsing bug on this side, and the two are impossible
        /// to separate after the response is discarded.
        /// </summary>
        public static List<string> ExtractOutputItemTypes(JObject parsed)
        {
            if (parsed["output"] is not JArray outputs) return new List<string>();

            return outputs
                .Select(item => item["type"]?.ToString())
                .Where(type => !string.IsNullOrWhiteSpace(type))
                .Select(type => type!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static List<string> ExtractSearchEvidence(JObject parsed)
        {
            var evidence = new List<string>();

            if (parsed["output"] is not JArray outputs) return evidence;

            foreach (var item in outputs)
            {
                if (item["type"]?.ToString()?.Contains(WebSearchToolType, StringComparison.OrdinalIgnoreCase) != true)
                    continue;

                var action = item["action"];
                if (action == null) continue;

                var kind = action["type"]?.ToString();
                if (string.IsNullOrWhiteSpace(kind)) kind = "search";

                var query = action["query"]?.ToString();
                var url = action["url"]?.ToString();

                if (!string.IsNullOrWhiteSpace(query))
                    evidence.Add($"{kind}: {query}");
                else if (!string.IsNullOrWhiteSpace(url))
                    evidence.Add($"{kind}: {url}");
                else
                    evidence.Add(kind);
            }

            return evidence;
        }

        /// <summary>
        /// Falls back to walking the Responses API output items when the convenience
        /// output_text field isn't present. Web search calls sit in the same array
        /// and carry no text, so they're skipped naturally.
        ///
        /// Only assistant answers count. The same array also carries reasoning
        /// items, whose content is the model's private chain of thought, and
        /// commentary messages it narrates between searches ("Let me check the
        /// remaining contacts"). Both have a text field, so taking every text
        /// field returned pages of prose with the answer buried in it — or, when
        /// the model ran out of budget before answering, pages of prose with no
        /// answer in it at all. Callers that expect JSON then fail on a reply
        /// that never contained any.
        /// </summary>
        private static string ExtractResponsesText(JObject parsed)
        {
            if (parsed["output"] is not JArray outputs) return "";

            var sb = new StringBuilder();

            foreach (var item in outputs)
            {
                if (!string.Equals(item["type"]?.ToString(), "message", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (string.Equals(item["phase"]?.ToString(), "commentary", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (item["content"] is not JArray contentArray) continue;

                foreach (var content in contentArray)
                {
                    // output_text is the answer; refusals and any other content
                    // type are not something a caller can parse.
                    if (!string.Equals(content["type"]?.ToString(), "output_text", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string? text = content["text"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(text))
                        sb.AppendLine(text.Trim());
                }
            }

            return sb.ToString().Trim();
        }
    }
}