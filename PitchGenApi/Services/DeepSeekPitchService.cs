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

        // Only deepseek-v4-flash serves the Responses API today; deepseek-v4-pro
        // does not. Used to add a hint when the API rejects the request.
        private const string ResponsesApiModel = "deepseek-v4-flash";

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
                    }
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
                    // Only deepseek-v4-flash serves this endpoint so far, and that is
                    // the likeliest cause of a rejection here.
                    string hint = apiModelName.Equals(ResponsesApiModel, StringComparison.OrdinalIgnoreCase)
                        ? ""
                        : $" (web search requires '{ResponsesApiModel}'; '{apiModelName}' is configured)";

                    return new PitchResult
                    {
                        Content = $"DeepSeek web search error ({(int)response.StatusCode}){hint}: {responseContent}",
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

            return outputs.Count(item =>
                string.Equals(
                    item["type"]?.ToString(),
                    WebSearchToolType + "_call",
                    StringComparison.OrdinalIgnoreCase));
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