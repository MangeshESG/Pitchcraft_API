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

        // Anthropic's server-side search tool, as served by DeepSeek's
        // Anthropic-compatible endpoint. The dated suffix is part of the type
        // and is not optional; the plain "web_search" spelling above is the
        // Responses API's name for a different thing.
        private const string AnthropicWebSearchToolType = "web_search_20250305";
        private const string AnthropicVersion = "2023-06-01";

        /// <summary>
        /// DeepSeek's hard ceiling on output tokens, and what every DeepSeek
        /// call asks for.
        ///
        /// The number is the API's own, quoted from the 400 it returns above
        /// it: "Invalid max_tokens value, the valid range of max_tokens is
        /// [1, 393216]". Measured 2026-09-17, identical for deepseek-flash and
        /// deepseek-v4-pro. /chat/completions enforces it; the Responses and
        /// Anthropic endpoints accept anything and clamp silently, so this is
        /// the one value that is correct on all three.
        /// </summary>
        private const int DeepSeekMaxOutputTokens = 393_216;

        /// <summary>
        /// The output ceiling for a call: the model's maximum, unless a caller
        /// or ModelRates asked for more.
        ///
        /// A ceiling is not a target -- output is billed as generated, not as
        /// reserved -- so there is nothing to save by setting it low, and
        /// setting it low is what truncated batches mid-JSON and lost them.
        /// Measured 2026-09-17 on a ten-contact research batch, raising it from
        /// 16,000 to the full 393,216 moved output from 1,494 tokens to 1,646
        /// and left the cost per batch unchanged.
        ///
        /// A configured value below the ceiling is therefore deliberately NOT
        /// honoured: ModelRates.MaxTokens is sized for writing one email, and
        /// on a research batch it only ever cut the answer short.
        /// </summary>
        private static int ResolveMaxTokens(int? requested, int? configured) =>
            Math.Max(DeepSeekMaxOutputTokens, Math.Max(requested ?? 0, configured ?? 0));

        // There is deliberately no max_uses on the search tool.
        //
        // It reads like a safety cap and behaves like a truncation. Flash fires
        // its searches in parallel, roughly one per contact, so a ten-contact
        // batch exceeds any small cap in its first round -- and exceeding it
        // does not degrade gracefully: the turn ends on stop_reason "tool_use"
        // with a max_uses_exceeded error and often no answer at all. Measured
        // 2026-09-17, max_uses 2 died that way on a six-contact batch.
        //
        // Uncapped, DeepSeek sizes the search to the work: measured 2026-09-17,
        // six contacts drew six searches, ten drew ten and fifteen drew fifteen,
        // all finishing cleanly. So nothing here limits how much it searches,
        // and nothing should.
        //
        // A max_uses_exceeded error still shows up in the transcript of runs
        // that finished perfectly well -- the model reaching past what it
        // needed, not a wall it hit -- so it is recorded as evidence and is not
        // treated as a failure. Only a turn that ends without an answer is a
        // problem, and the continuation below is for that. The work stays
        // bounded by max_tokens and the caller's batch timeout.

        // Turns per call. Not a limit on searching -- the model searches as much
        // as it likes on every one of them -- just a stop on a conversation that
        // never converges.
        //
        // Two was too few: measured 2026-09-18 on a ten-contact batch, a turn
        // that runs out mid-search can be followed by another that also runs out,
        // and the batch then failed with nothing to show for eighteen searches.
        // Across trials nothing needed more than two turns, so four is headroom
        // rather than an expectation, and unused turns cost nothing.
        private const int MaxSearchTurns = 4;

        /// <summary>
        /// Closes out a turn that ran out of searches mid-way. The searches it
        /// already ran are in the transcript, so this asks for the write-up
        /// rather than starting the batch over -- and says what to do about the
        /// contacts it never reached, which otherwise get quietly dropped from
        /// the JSON.
        /// </summary>
        private const string FinishWithoutSearchingInstruction =
            "Stop searching. Using only what you have already found above, write the " +
            "final answer now for every item requested, in exactly the format asked " +
            "for and with nothing else around it. For anything you could not verify, " +
            "say so in its comment and score it low rather than leaving it out.";

        // Evidence is an audit trail, not a copy of the results: each search
        // returns ten URLs, and all of them in the job record would bury the
        // queries that explain the score.
        private const int MaxEvidenceUrlsPerSearch = 3;

        // Hosted search on DeepSeek is an ENDPOINT capability, not a model one.
        // That distinction cost us a while, so it is written down here.
        //
        // The Responses compatibility table lists web_search among built-in
        // tools that are "Ignored", and that is accurate for /v1/responses:
        // measured 2026-09-16, deepseek-flash there returns zero
        // web_search_call items and says outright it has no browsing access.
        // Not because the tool was rejected — /v1/responses accepts it and
        // echoes it back normalised, with search_context_size and
        // user_location populated, exactly as it does for v4-pro — it is
        // simply never invoked. deepseek-v4-pro does run it on that endpoint.
        //
        // But DeepSeek also serves an Anthropic-compatible endpoint, and there
        // deepseek-flash runs hosted search properly: server_tool_use and
        // web_search_tool_result blocks, real URLs, and a
        // usage.server_tool_use.web_search_requests count. Same model, same
        // key, different endpoint. So Flash is NOT limited to caller-run
        // function tools for search, and needs no third-party search backend.
        //
        // Hence the split in GenerateWebSearchAsync below. Capability is still
        // never *trusted* from the model name: the request asks for search and
        // the caller checks WebSearchCalls on the way out, because a model that
        // skips the tool returns HTTP 200 with an answer written from memory,
        // which is indistinguishable from a real answer until counted.

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
                int maxTokens = ResolveMaxTokens(request.MaxTokens, rate?.MaxTokens);

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
            // Flash serves hosted search only on the Anthropic-compatible
            // endpoint (see the note at the top of this class), so route it
            // there rather than to /v1/responses, where the tool is accepted
            // and never invoked. Everything else stays on the Responses path,
            // which v4-pro runs searches on today.
            if (UsesAnthropicWebSearchPath(request?.ModelName))
                return await GenerateWebSearchViaAnthropicAsync(request!, clientId);

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
                int maxTokens = ResolveMaxTokens(request.MaxTokens, rate?.MaxTokens);

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
        /// <summary>
        /// Whether this model needs the Anthropic-compatible endpoint to run a
        /// hosted web search.
        ///
        /// Measured 2026-09-16 on the same key: deepseek-flash (and its alias
        /// deepseek-v4-flash -- /models lists only deepseek-flash and
        /// deepseek-v4-pro) runs no searches on /v1/responses but runs them
        /// normally on /anthropic/v1/messages. deepseek-v4-pro already searches
        /// on /v1/responses, so it is left there.
        ///
        /// Routing only. If DeepSeek later serves search for Flash on the
        /// Responses API, deleting this returns it to the shared path with no
        /// other change -- the WebSearchCalls count is what proves a search ran
        /// either way.
        /// </summary>
        private static bool UsesAnthropicWebSearchPath(string? modelName) =>
            modelName?.Contains("flash", StringComparison.OrdinalIgnoreCase) == true;

        /// <summary>
        /// Hosted web search for Flash, over DeepSeek's Anthropic-compatible
        /// endpoint. This is the same kind of server-side tool the OpenAI path
        /// uses -- DeepSeek runs the searches and feeds the results back to the
        /// model itself, so there is no caller-run search loop and no
        /// third-party search backend. Pass clientId 0 to skip the deduction.
        /// </summary>
        private async Task<PitchResult> GenerateWebSearchViaAnthropicAsync(
            EnquiryRequest request,
            int clientId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request.Prompt))
                    return new PitchResult { Content = "Prompt is required.", IsSuccess = false };

                var requestedModelName = (request.ModelName ?? "").Trim();

                if (requestedModelName.Length == 0)
                    return new PitchResult { Content = "Model name is required.", IsSuccess = false };

                var apiModelName = requestedModelName.EndsWith(
                    "-thinking", StringComparison.OrdinalIgnoreCase)
                    ? requestedModelName.Replace("-thinking", "", StringComparison.OrdinalIgnoreCase)
                    : requestedModelName;

                var rate =
                    await _context.ModelRates.FirstOrDefaultAsync(m => m.ModelName == requestedModelName)
                    ?? await _context.ModelRates.FirstOrDefaultAsync(m => m.ModelName == apiModelName);

                decimal inputPricePerMillion = rate?.InputPrice ?? 0.27m;
                decimal outputPricePerMillion = rate?.OutputPrice ?? 1.10m;
                int maxTokens = ResolveMaxTokens(request.MaxTokens, rate?.MaxTokens);

                var searchTool = new Dictionary<string, object>
                {
                    { "type", AnthropicWebSearchToolType },
                    { "name", "web_search" }
                };

                var messages = new List<object>
                {
                    new { role = "user", content = request.Prompt }
                };

                // Everything below accumulates across turns, because a run that
                // has to be continued still searched and still cost tokens on
                // the turn that ran out.
                int promptTokens = 0, completionTokens = 0, cachedTokens = 0, searchCalls = 0;
                var evidence = new List<string>();
                var blockTypes = new List<string>();
                string output = "";
                string stopReason = "";
                string? searchError = null;

                for (var attempt = 1; attempt <= MaxSearchTurns; attempt++)
                {
                    var requestBody = new Dictionary<string, object>
                    {
                        { "model", apiModelName },
                        { "max_tokens", maxTokens },
                        { "messages", messages },
                        { "tools", new object[] { searchTool } }
                    };

                    if (!string.IsNullOrWhiteSpace(request.ScrappedData))
                        requestBody["system"] = request.ScrappedData;

                    // Deliberately no tool_choice, on either turn. The closing
                    // turn could be told not to search, but measured 2026-09-17
                    // it runs zero searches whether or not it is forbidden to --
                    // asked to write up what it found, it writes up what it
                    // found. Forbidding it only removes the model's option to
                    // check one last thing it decides it needs.

                    using var httpRequest = new HttpRequestMessage(
                        HttpMethod.Post, $"{_baseUrl}/anthropic/v1/messages");

                    // The bearer token on the shared client authenticates here
                    // too; this endpoint additionally requires the Anthropic
                    // version header, which the Responses path does not send.
                    httpRequest.Headers.Add("anthropic-version", AnthropicVersion);
                    httpRequest.Content = new StringContent(
                        JsonConvert.SerializeObject(requestBody), Encoding.UTF8, "application/json");

                    var response = await _httpClient.SendAsync(httpRequest);
                    var responseContent = await response.Content.ReadAsStringAsync();

                    if (!response.IsSuccessStatusCode)
                    {
                        return new PitchResult
                        {
                            Content = $"DeepSeek web search error ({(int)response.StatusCode}) "
                                    + $"on model '{apiModelName}': {responseContent}",
                            IsSuccess = false
                        };
                    }

                    var parsed = JObject.Parse(responseContent);

                    promptTokens += parsed.SelectToken("usage.input_tokens")?.Value<int>() ?? 0;
                    completionTokens += parsed.SelectToken("usage.output_tokens")?.Value<int>() ?? 0;
                    cachedTokens += parsed.SelectToken("usage.cache_read_input_tokens")?.Value<int>() ?? 0;
                    searchCalls += CountAnthropicWebSearchCalls(parsed);
                    evidence.AddRange(ExtractAnthropicSearchEvidence(parsed));
                    searchError ??= FirstSearchErrorCode(parsed);

                    foreach (var type in ExtractAnthropicBlockTypes(parsed))
                    {
                        if (!blockTypes.Contains(type, StringComparer.OrdinalIgnoreCase))
                            blockTypes.Add(type);
                    }

                    output = ExtractAnthropicText(parsed);
                    stopReason = parsed["stop_reason"]?.ToString() ?? "";

                    // "tool_use" means the turn ended on a tool rather than on
                    // an answer -- the model was still mid-search when the turn
                    // stopped. Nothing in the request causes this and nothing in
                    // the request prevents it; it is decided server-side.
                    //
                    // The searches themselves already succeeded and are in the
                    // transcript, so the fix is to hand the transcript back and
                    // ask for the write-up rather than to pay for the whole
                    // batch again. Note the text at this point may be a non-empty
                    // preamble ("I'll verify each contact...") rather than empty,
                    // which is why this keys off stop_reason and not the text.
                    if (!string.Equals(stopReason, "tool_use", StringComparison.OrdinalIgnoreCase))
                        break;

                    if (attempt == MaxSearchTurns) break;

                    if (parsed["content"] is JArray assistantBlocks)
                        messages.Add(new { role = "assistant", content = assistantBlocks });

                    messages.Add(new { role = "user", content = FinishWithoutSearchingInstruction });
                }

                // As on the other paths, DeepSeek bills search as the extra
                // model tokens it consumes, so there is no per-search fee here.
                decimal currentCost =
                    (promptTokens * inputPricePerMillion / 1_000_000m) +
                    (completionTokens * outputPricePerMillion / 1_000_000m);

                // Anthropic's spelling of "ran out of budget". Same trap as
                // finish_reason "length" on /chat/completions: the JSON comes
                // back cut off mid-array and reads downstream as a formatting
                // fault rather than a budget one.
                if (string.Equals(stopReason, "max_tokens", StringComparison.OrdinalIgnoreCase))
                {
                    return new PitchResult
                    {
                        Content = $"Response truncated: hit max_tokens ({maxTokens}) after "
                                + $"{searchCalls} search(es). Raise MaxTokens for this model in "
                                + "ModelRates, or ask for less in one call.",
                        IsSuccess = false,
                        PromptTokens = promptTokens,
                        CompletionTokens = completionTokens,
                        TotalTokens = promptTokens + completionTokens,
                        CachedTokens = cachedTokens,
                        CurrentCost = currentCost,
                        WebSearchCalls = searchCalls,
                        SearchEvidence = evidence,
                        OutputItemTypes = blockTypes
                    };
                }

                // A turn that ends on "tool_use" never wrote an answer, whatever
                // text it left behind -- and it usually leaves some: the running
                // commentary, "I'll research each contact... Let me continue
                // searching...". That text is not a result, so returning it as a
                // success just moves the failure downstream, where it surfaces as
                // "the model's reply could not be read as JSON results" and sends
                // the reader to a parser that is working correctly.
                var ranOutMidSearch =
                    string.Equals(stopReason, "tool_use", StringComparison.OrdinalIgnoreCase);

                if (ranOutMidSearch || string.IsNullOrWhiteSpace(output))
                {
                    var noContent =
                        ranOutMidSearch
                            ? $"The model was still searching after {searchCalls} search(es) across "
                              + $"{MaxSearchTurns} turns and never wrote an answer"
                              + (string.IsNullOrWhiteSpace(searchError)
                                  ? ". The batch may be too large for one call."
                                  : $": the search tool reported '{searchError}'.")
                            : string.IsNullOrWhiteSpace(stopReason)
                                ? "The model returned no content."
                                : $"The model returned no content (stop_reason: {stopReason}).";

                    return new PitchResult
                    {
                        Content = noContent,
                        IsSuccess = false,
                        PromptTokens = promptTokens,
                        CompletionTokens = completionTokens,
                        TotalTokens = promptTokens + completionTokens,
                        CachedTokens = cachedTokens,
                        CurrentCost = currentCost,
                        WebSearchCalls = searchCalls,
                        SearchEvidence = evidence,
                        OutputItemTypes = blockTypes
                    };
                }

                if (clientId > 0)
                    await _contactRepository.CreditDeduction(clientId, 1);

                return new PitchResult
                {
                    Content = output,
                    PromptTokens = promptTokens,
                    CompletionTokens = completionTokens,
                    TotalTokens = promptTokens + completionTokens,
                    CachedTokens = cachedTokens,
                    CurrentCost = currentCost,
                    IsSuccess = true,
                    WebSearchCalls = searchCalls,
                    SearchEvidence = evidence,
                    OutputItemTypes = blockTypes
                };
            }
            catch (TaskCanceledException ex)
            {
                return new PitchResult
                {
                    Content = $"DeepSeek web search timed out after "
                            + $"{_httpClient.Timeout.TotalSeconds} seconds: {ex.Message}",
                    IsSuccess = false
                };
            }
            catch (Exception ex)
            {
                return new PitchResult
                {
                    Content = "DeepSeek web search failed: " + Describe(ex),
                    IsSuccess = false
                };
            }
        }

        /// <summary>Assistant text, which on this endpoint is spread across the "text" content blocks.</summary>
        private static string ExtractAnthropicText(JObject parsed)
        {
            if (parsed["content"] is not JArray blocks) return "";

            var text = new StringBuilder();

            foreach (var block in blocks)
            {
                if (block["type"]?.ToString() == "text")
                    text.Append(block["text"]?.ToString());
            }

            return text.ToString().Trim();
        }

        /// <summary>
        /// Searches actually performed. DeepSeek reports the count directly in
        /// usage, which is authoritative; counting server_tool_use blocks is the
        /// fallback for a response that omits it.
        /// </summary>
        private static int CountAnthropicWebSearchCalls(JObject parsed)
        {
            var reported = parsed.SelectToken("usage.server_tool_use.web_search_requests")?.Value<int>();
            if (reported.HasValue) return reported.Value;

            if (parsed["content"] is not JArray blocks) return 0;

            return blocks.Count(b => b["type"]?.ToString() == "server_tool_use");
        }

        /// <summary>
        /// What was searched for and which pages came back, mirroring the
        /// Responses-path extractor so a stored score stays auditable whichever
        /// endpoint produced it.
        /// </summary>
        private static List<string> ExtractAnthropicSearchEvidence(JObject parsed)
        {
            var evidence = new List<string>();

            if (parsed["content"] is not JArray blocks) return evidence;

            foreach (var block in blocks)
            {
                switch (block["type"]?.ToString())
                {
                    case "server_tool_use":
                        var query = block.SelectToken("input.query")?.ToString();
                        if (!string.IsNullOrWhiteSpace(query))
                            evidence.Add("search: " + query);
                        break;

                    case "web_search_tool_result":
                        // An errored search reports the failure in place of its
                        // results -- sometimes as a bare object, but normally as
                        // a single error entry inside the content array, which
                        // is why this checks both shapes. Worth recording: it is
                        // the difference between "found nothing" and "never
                        // looked".
                        if (block["content"] is not JArray results)
                        {
                            var error = block.SelectToken("content.error_code")?.ToString();
                            if (!string.IsNullOrWhiteSpace(error))
                                evidence.Add("search failed: " + error);
                            break;
                        }

                        var inlineError = results
                            .Select(r => r["error_code"]?.ToString())
                            .FirstOrDefault(e => !string.IsNullOrWhiteSpace(e));

                        if (!string.IsNullOrWhiteSpace(inlineError))
                        {
                            evidence.Add("search failed: " + inlineError);
                            break;
                        }

                        foreach (var url in results
                                     .Select(r => r["url"]?.ToString())
                                     .Where(u => !string.IsNullOrWhiteSpace(u))
                                     .Take(MaxEvidenceUrlsPerSearch))
                        {
                            evidence.Add("opened: " + url);
                        }
                        break;
                }
            }

            return evidence;
        }

        /// <summary>
        /// The first error code any search reported, or null if none did. Used
        /// to explain a turn that ended mid-search.
        /// </summary>
        private static string? FirstSearchErrorCode(JObject parsed)
        {
            if (parsed["content"] is not JArray blocks) return null;

            foreach (var block in blocks)
            {
                if (block["type"]?.ToString() != "web_search_tool_result") continue;

                var direct = block.SelectToken("content.error_code")?.ToString();
                if (!string.IsNullOrWhiteSpace(direct)) return direct;

                if (block["content"] is not JArray results) continue;

                var inline = results
                    .Select(r => r["error_code"]?.ToString())
                    .FirstOrDefault(e => !string.IsNullOrWhiteSpace(e));

                if (!string.IsNullOrWhiteSpace(inline)) return inline;
            }

            return null;
        }

        /// <summary>Distinct content block types, for telling a skipped search apart from an unrecognised one.</summary>
        private static List<string> ExtractAnthropicBlockTypes(JObject parsed)
        {
            if (parsed["content"] is not JArray blocks) return new List<string>();

            return blocks
                .Select(b => b["type"]?.ToString())
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

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