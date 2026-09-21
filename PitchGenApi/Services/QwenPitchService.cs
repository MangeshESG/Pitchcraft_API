using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PitchGenApi.Database;
using PitchGenApi.Model;

namespace PitchGenApi.Services
{
    /// <summary>
    /// Qwen, served from Alibaba Cloud Model Studio (DashScope) through its
    /// OpenAI-compatible endpoints. Deliberately a copy of the shape
    /// <see cref="DeepSeekPitchService"/> exposes — GeneratePitchAsync and
    /// GenerateWebSearchAsync returning a <see cref="PitchResult"/> — so the
    /// callers only gain one more branch in the provider ternary they already
    /// have, and nothing downstream of them changes.
    /// </summary>
    public class QwenPitchService
    {
        // Model Studio's built-in server-side search tool on the Responses API,
        // alongside web_extractor and code_interpreter. Not to be confused with
        // the chat-completions "enable_search" flag, which turns on the same
        // capability through a different endpoint — see SearchViaChatAsync.
        private const string WebSearchToolType = "web_search";

        // Reasoning effort for the Responses API. "medium" and "low" were both
        // accepted; "high" is not — Model Studio maps effort onto its own
        // thinking_budget and rejects that one with "The thinking_budget
        // parameter must be a positive integer and not greater than 131072"
        // (HTTP 400, measured 2026-09-11). Medium is therefore the ceiling, not
        // a tuning choice.
        private static readonly object ReasoningEffort = new { effort = "medium" };

        /// <summary>
        /// What actually makes Qwen search.
        ///
        /// tool_choice does not — on qwen3.6 it is inert and on qwen3.8 it is a
        /// hard error, so it is not sent at all (see the request builder).
        /// Measured 2026-09-11 on 3.6: "required", an explicit
        /// {type:"web_search"} object, and plain auto all produce the identical
        /// response on a prompt the model can answer from memory — reasoning and
        /// a message, no web_search_call, no usage.x_tools. The field is accepted
        /// and echoed back untouched, so it looks honoured and is not. Qwen
        /// decides for itself whether a question needs the web, which is fine
        /// until the question is one it wrongly believes it knows.
        ///
        /// That is exactly the failure the zero-search guard in
        /// ContactValidationService exists to catch, and on a research check it
        /// costs the batch. This instruction is the only lever that moves it:
        /// with it in place, both qwen3.6-plus and qwen3.6-flash searched even
        /// when asked what 2 plus 2 is.
        /// </summary>
        private const string ForceSearchInstruction =
            "You must call the web_search tool at least once before answering, even if you "
          + "believe you already know the answer. An answer given without searching is not "
          + "acceptable.";

        /// <summary>
        /// The same demand, put more bluntly, for a retry after the model
        /// answered from memory anyway.
        ///
        /// It also grants the thing the model is most likely to have talked
        /// itself out of. Measured on the batch shape that failed in Audience
        /// Assurance, the skipped runs were ones where the records were too
        /// sparse to look up — no company, just a name — and the model appears
        /// to reason that a search it expects to find nothing is not worth
        /// running. A search that finds nothing is exactly what this caller
        /// needs: it is the evidence that the contact could not be confirmed,
        /// and it is not the same answer as a guess.
        /// </summary>
        private const string ForceSearchInstructionEscalated =
            "Before you write any part of your answer you must call the web_search tool at "
          + "least once, and you should call it once for every item you are asked about. "
          + "This applies even when you believe you already know the answer and even when "
          + "you expect the search to find nothing: a search that returns nothing is a "
          + "valid and useful result, because it is the evidence that the item could not "
          + "be confirmed. Answering from memory without searching is a failed response.";

        /// <summary>
        /// How many times to ask before giving up on getting a search.
        ///
        /// Needed because the search cannot be forced. tool_choice is inert
        /// here (see ForceSearchInstruction) and the instruction only persuades:
        /// measured over 12 runs of the batch shape that failed in production,
        /// qwen3.6-flash skipped the search once — about 8%, which across the
        /// several batches of a real validation run is near enough to certain.
        /// Three attempts takes that to roughly one in two thousand.
        ///
        /// Retries only happen on the runs that skipped, so the expected cost is
        /// a few percent, and it buys back a batch that would otherwise be
        /// failed outright and its credits refunded.
        /// </summary>
        private const int MaxSearchAttempts = 3;

        // Two endpoints reach Qwen's web search and they are not interchangeable:
        //
        //   /responses         built-in web_search tool. Returns the tool trace —
        //                      one web_search_call item per search, each carrying
        //                      the query or the URL opened. Documented for the
        //                      agent-capable models (qwen3.5-plus/flash,
        //                      qwen3.6-plus, qwen3.7-plus, qwen3-max,
        //                      qwen3.7-max, qwen3.8-max/flash); everything else
        //                      is listed as "limited".
        //
        //   /chat/completions  enable_search + search_options. Works across the
        //                      wider Qwen line, but Model Studio states plainly
        //                      that this endpoint does not return search sources.
        //
        // The Responses path runs first because a countable trace is what makes
        // a researched answer distinguishable from one written from memory. The
        // chat path is the fallback for models the Responses API will not serve,
        // and it is labelled as such on the way out so a zero-trace result is
        // never mistaken for a model that skipped the search.

        private readonly HttpClient _httpClient;
        private readonly AppDbContext _context;
        private readonly ContactRepository _contactRepository;
        private readonly string _apiKey;
        private readonly string _baseUrl;
        private readonly decimal _searchCostPerThousandCalls;
        private readonly string _searchStrategy;

        public QwenPitchService(
            HttpClient httpClient,
            AppDbContext context,
            ContactRepository contactRepository,
            IOptions<QwenSettings> options)
        {
            _httpClient = httpClient;
            _context = context;
            _contactRepository = contactRepository;
            _apiKey = options.Value.ApiKey;

            _baseUrl = string.IsNullOrWhiteSpace(options.Value.BaseUrl)
                ? "https://dashscope-intl.aliyuncs.com/compatible-mode/v1"
                : options.Value.BaseUrl.TrimEnd('/');

            _searchCostPerThousandCalls = options.Value.SearchCostPerThousandCalls;

            // "turbo", not "agent": agent forces thinking mode, which the
            // non-streaming chat fallback is refused for. See QwenSettings.
            _searchStrategy = string.IsNullOrWhiteSpace(options.Value.SearchStrategy)
                ? "turbo"
                : options.Value.SearchStrategy.Trim();

            // Ten minutes, matching the OpenAI client in Program.cs and the
            // validation runner's own client rather than being generous for its
            // own sake.
            //
            // Three minutes was not a budget anyone measured a call against, and
            // a researched batch outgrew it: at the configured batch size of 50
            // contacts, one web-search call runs a search per contact and the
            // whole turn regularly passes 180s, which surfaced as "Qwen web
            // search timed out after 180 seconds" on work that was progressing
            // perfectly well. The request is abandoned at that point but the
            // provider still ran and still billed it, so the short ceiling cost
            // the batch and the money both.
            //
            // Not removed altogether: with no ceiling a wedged call holds its
            // runner slot forever, and Timeout.InfiniteTimeSpan is how a queue
            // stops draining. Ten minutes is long enough that only a genuinely
            // stuck call reaches it.
            //
            // Note this is the ONLY thing that stops a long Qwen call. The
            // per-batch CancellationToken in ContactValidationService is passed
            // to CallModelAsync but not onward into this service, so
            // Validation:ModelCallTimeoutSeconds does not apply to the Qwen or
            // DeepSeek paths - see Validation:StaleJobMinutes in appsettings,
            // which has to stay above this value.
            _httpClient.Timeout = TimeSpan.FromMinutes(10);

            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");
            _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
        }

        /// <summary>
        /// Flattens the exception chain into one line, for the same reason the
        /// DeepSeek service does: a TLS failure reports only "The SSL connection
        /// could not be established" at the top level, and the sentence naming
        /// the real cause sits in the inner exception.
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

        /// <summary>
        /// Strips the "-thinking" suffix this codebase uses to mean "same model,
        /// reasoning on". Model Studio has no such model name; the suffix is our
        /// own convention and has to be resolved before the name reaches the API.
        /// </summary>
        private static (string ApiModelName, bool ThinkingEnabled) ResolveModelName(string requestedModelName)
        {
            bool thinking = requestedModelName.EndsWith("-thinking", StringComparison.OrdinalIgnoreCase);

            return (
                thinking
                    ? requestedModelName.Replace("-thinking", "", StringComparison.OrdinalIgnoreCase)
                    : requestedModelName,
                thinking
            );
        }

        private async Task<ModelRate?> LookupRateAsync(string requestedModelName, string apiModelName) =>
            await _context.ModelRates.FirstOrDefaultAsync(m => m.ModelName == requestedModelName)
            ?? await _context.ModelRates.FirstOrDefaultAsync(m => m.ModelName == apiModelName);

        // =====================================================================
        // Generation
        // =====================================================================

        public async Task<PitchResult> GeneratePitchAsync(EnquiryRequest request)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request?.Prompt))
                    return new PitchResult { Content = "Prompt is required.", IsSuccess = false };

                if (string.IsNullOrWhiteSpace(request.ModelName))
                    return new PitchResult { Content = "Model name is required.", IsSuccess = false };

                string requestedModelName = request.ModelName.Trim();
                var (apiModelName, thinkingEnabled) = ResolveModelName(requestedModelName);

                var rate = await LookupRateAsync(requestedModelName, apiModelName);

                decimal inputPricePerMillion = rate?.InputPrice ?? 0.325m;
                decimal outputPricePerMillion = rate?.OutputPrice ?? 1.95m;
                double temperature = Convert.ToDouble(rate?.Temperature ?? 0.7m);

                // The caller's budget wins over the rate row, which is sized for
                // one email and far too small for a batched JSON reply.
                int maxTokens = request.MaxTokens ?? rate?.MaxTokens ?? 2000;

                // Thinking cannot be asked for on this endpoint. Model Studio
                // rejects a non-streaming chat completion that sets
                // enable_thinking true — "parameter.enable_thinking must be set
                // to false for non-streaming calls", HTTP 400 — and every call
                // in this codebase is non-streaming. The Responses API has no
                // such rule, so a "-thinking" model is routed there with a
                // reasoning effort instead of being failed outright or silently
                // downgraded to a non-reasoning reply.
                if (thinkingEnabled)
                {
                    return await ReasoningViaResponsesAsync(
                        request, apiModelName, maxTokens,
                        inputPricePerMillion, outputPricePerMillion);
                }

                var messages = new List<object>();

                if (!string.IsNullOrWhiteSpace(request.ScrappedData))
                {
                    messages.Add(new { role = "system", content = request.ScrappedData });
                }

                messages.Add(new { role = "user", content = request.Prompt });

                var requestBody = new Dictionary<string, object>
                {
                    { "model", apiModelName },
                    { "messages", messages },
                    { "max_tokens", maxTokens },
                    { "temperature", temperature },
                    { "stream", false },
                    // Sent explicitly rather than left to the default: the
                    // hybrid Qwen3 models reject a non-streaming call that does
                    // not settle this one way or the other.
                    { "enable_thinking", false }
                };

                using var httpContent = new StringContent(
                    JsonConvert.SerializeObject(requestBody),
                    Encoding.UTF8,
                    "application/json"
                );

                var response = await _httpClient.PostAsync($"{_baseUrl}/chat/completions", httpContent);
                var responseContent = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    return new PitchResult
                    {
                        Content = $"Qwen API Error ({(int)response.StatusCode}) on model "
                                + $"'{apiModelName}': {responseContent}",
                        IsSuccess = false
                    };
                }

                var parsed = JObject.Parse(responseContent);

                string output = parsed["choices"]?[0]?["message"]?["content"]?.ToString() ?? "";

                int promptTokens = parsed["usage"]?["prompt_tokens"]?.Value<int>() ?? 0;
                int completionTokens = parsed["usage"]?["completion_tokens"]?.Value<int>() ?? 0;
                int totalTokens = parsed["usage"]?["total_tokens"]?.Value<int>()
                                  ?? promptTokens + completionTokens;
                int cachedTokens =
                    parsed.SelectToken("usage.prompt_tokens_details.cached_tokens")?.Value<int>() ?? 0;

                // A 200 can still be a generation that ran out of budget, and the
                // only place that is said is finish_reason. Returning it as a
                // success hands the caller JSON cut off mid-array, which then
                // fails somewhere far less obvious.
                string finishReason = parsed["choices"]?[0]?["finish_reason"]?.ToString() ?? "";

                if (string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase))
                {
                    return new PitchResult
                    {
                        Content = $"Response truncated: hit max_tokens ({maxTokens}). "
                                + "Raise MaxTokens for this model in ModelRates, or ask for less in one call.",
                        IsSuccess = false,
                        PromptTokens = promptTokens,
                        CompletionTokens = completionTokens,
                        CachedTokens = cachedTokens
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
                        PromptTokens = promptTokens,
                        CompletionTokens = completionTokens,
                        CachedTokens = cachedTokens
                    };
                }

                return new PitchResult
                {
                    Content = output,
                    PromptTokens = promptTokens,
                    CompletionTokens = completionTokens,
                    TotalTokens = totalTokens,
                    CachedTokens = cachedTokens,
                    CurrentCost = TokenCost(promptTokens, completionTokens,
                                            inputPricePerMillion, outputPricePerMillion),
                    IsSuccess = true
                };
            }
            catch (TaskCanceledException ex)
            {
                return new PitchResult
                {
                    Content = $"Qwen request timed out after {_httpClient.Timeout.TotalSeconds} seconds: {ex.Message}",
                    IsSuccess = false
                };
            }
            catch (Exception ex)
            {
                return new PitchResult
                {
                    Content = $"Qwen request failed: {Describe(ex)}",
                    IsSuccess = false
                };
            }
        }

        // =====================================================================
        // Web search
        // =====================================================================

        /// <summary>
        /// Runs the research step (the one that fills {web_searched_data}) on Qwen.
        /// Goes to the Responses API with the built-in web_search tool, and falls
        /// back to chat completions with enable_search when the configured model
        /// is not served there. Pass clientId 0 to skip the credit deduction.
        /// </summary>
        public async Task<PitchResult> GenerateWebSearchAsync(EnquiryRequest request, int clientId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request?.Prompt))
                    return new PitchResult { Content = "Prompt is required.", IsSuccess = false };

                string requestedModelName = (request.ModelName ?? "").Trim();

                if (requestedModelName.Length == 0)
                    return new PitchResult { Content = "Model name is required.", IsSuccess = false };

                var (apiModelName, thinkingEnabled) = ResolveModelName(requestedModelName);

                var rate = await LookupRateAsync(requestedModelName, apiModelName);

                decimal inputPricePerMillion = rate?.InputPrice ?? 0.325m;
                decimal outputPricePerMillion = rate?.OutputPrice ?? 1.95m;
                int maxTokens = request.MaxTokens ?? rate?.MaxTokens ?? 2000;

                // Tokens burned by attempts that answered without searching and
                // were therefore thrown away. Carried onto whichever attempt is
                // finally returned: the provider billed them, so a caller adding
                // this result to a job's running total has to see them or the
                // run under-reports its own cost.
                int discardedPromptTokens = 0;
                int discardedCompletionTokens = 0;
                decimal discardedCost = 0m;

                SearchAttempt attempt;

                for (int attemptNumber = 1; ; attemptNumber++)
                {
                    attempt = await SearchViaResponsesAsync(
                        request, apiModelName, thinkingEnabled, maxTokens,
                        inputPricePerMillion, outputPricePerMillion,
                        escalate: attemptNumber > 1);

                    // The endpoint refused the request, or answered and failed
                    // for a reason retrying cannot mend. Either way, stop.
                    if (attempt.RetryOnChatEndpoint || !attempt.Pitch.IsSuccess)
                        break;

                    if (attempt.Pitch.WebSearchCalls > 0)
                        break;

                    if (attemptNumber >= MaxSearchAttempts)
                        break;

                    discardedPromptTokens += attempt.Pitch.PromptTokens;
                    discardedCompletionTokens += attempt.Pitch.CompletionTokens;
                    discardedCost += attempt.Pitch.CurrentCost;
                }

                if (discardedPromptTokens > 0 || discardedCompletionTokens > 0)
                {
                    attempt.Pitch.PromptTokens += discardedPromptTokens;
                    attempt.Pitch.CompletionTokens += discardedCompletionTokens;
                    attempt.Pitch.TotalTokens += discardedPromptTokens + discardedCompletionTokens;
                    attempt.Pitch.CurrentCost += discardedCost;
                }

                // Only a rejection of the request itself is worth trying the
                // other endpoint. A 200 that came back thin is a budget or
                // prompt problem, and re-running it on a weaker endpoint would
                // just spend the tokens twice.
                if (attempt.RetryOnChatEndpoint)
                {
                    attempt = await SearchViaChatAsync(
                        request, apiModelName, maxTokens,
                        inputPricePerMillion, outputPricePerMillion,
                        attempt.Pitch.Content);
                }

                if (attempt.Pitch.IsSuccess && clientId > 0)
                {
                    await _contactRepository.CreditDeduction(clientId);
                }

                return attempt.Pitch;
            }
            catch (TaskCanceledException ex)
            {
                return new PitchResult
                {
                    Content = $"Qwen web search timed out after {_httpClient.Timeout.TotalSeconds} seconds: {ex.Message}",
                    IsSuccess = false
                };
            }
            catch (Exception ex)
            {
                return new PitchResult
                {
                    Content = $"Qwen web search failed: {Describe(ex)}",
                    IsSuccess = false
                };
            }
        }

        /// <summary>
        /// A result plus whether the chat-completions fallback should be tried.
        /// The flag is separate from IsSuccess because most failures must not be
        /// retried — only the ones that mean "this endpoint will not serve this
        /// model" should be.
        /// </summary>
        private sealed class SearchAttempt
        {
            public PitchResult Pitch { get; init; } = new();
            public bool RetryOnChatEndpoint { get; init; }
        }

        private async Task<SearchAttempt> SearchViaResponsesAsync(
            EnquiryRequest request,
            string apiModelName,
            bool thinkingEnabled,
            int maxTokens,
            decimal inputPricePerMillion,
            decimal outputPricePerMillion,
            bool escalate)
        {
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

                // No tool_choice. It cannot help and on the current models it
                // breaks the call outright:
                //
                //   qwen3.6  accepts "required" and ignores it — the response is
                //            byte-for-byte what auto returns, so it only looked
                //            like it was forcing a search.
                //   qwen3.8  rejects it: HTTP 400, "The tool_choice parameter
                //            does not support being set to required or object in
                //            thinking mode", and these models think by default.
                //
                // Measured 2026-09-15 on qwen3.8-flash and qwen3.8-max. Omitting
                // it searches on both; "auto" also works but says nothing that
                // the default does not. ForceSearchInstruction is what actually
                // gets the search, and it is the only thing that does.
            };

            if (thinkingEnabled)
            {
                requestBody["reasoning"] = ReasoningEffort;
            }

            // Always set, unlike the caller's own instructions, because the
            // search directive has to reach the model whether or not the caller
            // supplied any context of its own. The blunter wording is kept for
            // the retry rather than used from the start: it does get a search
            // every time, but it also gets more of them — measured across the
            // same batch, roughly 2.5 searches against 1.6 — and every search
            // carries its own fee. Paying that on every call to avoid a one in
            // twelve retry is the wrong way round.
            string directive = escalate
                ? ForceSearchInstructionEscalated
                : ForceSearchInstruction;

            requestBody["instructions"] = string.IsNullOrWhiteSpace(request.ScrappedData)
                ? directive
                : directive + "\n\n" + request.ScrappedData;

            using var httpContent = new StringContent(
                JsonConvert.SerializeObject(requestBody),
                Encoding.UTF8,
                "application/json"
            );

            var response = await _httpClient.PostAsync($"{_baseUrl}/responses", httpContent);
            var responseContent = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                int statusCode = (int)response.StatusCode;

                // 400 and 404 here mean the model is not served by the Responses
                // API, or will not take a built-in tool or tool_choice. All of
                // those are answerable on the chat endpoint. A 401/429/5xx is
                // not — retrying those would only fail again more slowly.
                bool retry = statusCode == 400 || statusCode == 404;

                return new SearchAttempt
                {
                    RetryOnChatEndpoint = retry,
                    Pitch = new PitchResult
                    {
                        Content = $"Qwen web search error ({statusCode}) on model "
                                + $"'{apiModelName}' at {_baseUrl}/responses: {responseContent}",
                        IsSuccess = false
                    }
                };
            }

            var parsed = JObject.Parse(responseContent);

            string output = parsed["output_text"]?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(output))
                output = ExtractResponsesText(parsed);

            int promptTokens = parsed["usage"]?["input_tokens"]?.Value<int>() ?? 0;
            int completionTokens = parsed["usage"]?["output_tokens"]?.Value<int>() ?? 0;
            int totalTokens = parsed["usage"]?["total_tokens"]?.Value<int>()
                              ?? promptTokens + completionTokens;

            int cachedTokens =
                parsed.SelectToken("usage.input_tokens_details.cached_tokens")?.Value<int>() ?? 0;

            int searchCalls = CountWebSearchCalls(parsed);

            decimal currentCost =
                TokenCost(promptTokens, completionTokens, inputPricePerMillion, outputPricePerMillion)
                + SearchCost(searchCalls);

            // A run that spends its whole output budget researching comes back
            // status "incomplete" — or simply with the search calls and no
            // closing answer — and the tokens are spent either way. Saying so
            // here is what sends the next reader to the budget rather than to
            // the parser.
            string status = parsed["status"]?.ToString() ?? "";

            // SelectToken, not parsed["incomplete_details"]?["reason"]: the field
            // is present and JSON null on every complete response, and a JValue
            // holding null is not a C# null, so ?. does not short circuit and
            // indexing into it throws.
            string incompleteReason =
                parsed.SelectToken("incomplete_details.reason")?.ToString() ?? "";

            bool ranOut =
                status.Equals("incomplete", StringComparison.OrdinalIgnoreCase) ||
                incompleteReason.Length > 0;

            if (ranOut || string.IsNullOrWhiteSpace(output))
            {
                string reason = ranOut
                    ? $"Qwen stopped before answering (status '{status}'"
                      + (incompleteReason.Length > 0 ? $", reason '{incompleteReason}'" : "")
                      + $"): the {maxTokens:N0} token output budget was spent on reasoning and "
                      + $"{searchCalls} web searches. Raise max_output_tokens or reduce the batch size."
                    : $"Qwen returned no answer text after {searchCalls} web searches and "
                      + $"{completionTokens:N0} output tokens against a {maxTokens:N0} token budget.";

                // Tokens and cost are still reported: the caller adds them to the
                // job before it checks IsSuccess, so a failed batch is billed as
                // accurately as a successful one. And no retry — the endpoint
                // answered, it just could not finish.
                return new SearchAttempt
                {
                    Pitch = new PitchResult
                    {
                        Content = reason,
                        PromptTokens = promptTokens,
                        CompletionTokens = completionTokens,
                        TotalTokens = totalTokens,
                        CachedTokens = cachedTokens,
                        WebSearchCalls = searchCalls,
                        SearchEvidence = ExtractSearchEvidence(parsed),
                        OutputItemTypes = ExtractOutputItemTypes(parsed),
                        CurrentCost = currentCost,
                        IsSuccess = false
                    }
                };
            }

            return new SearchAttempt
            {
                Pitch = new PitchResult
                {
                    Content = output,
                    PromptTokens = promptTokens,
                    CompletionTokens = completionTokens,
                    TotalTokens = totalTokens,
                    CachedTokens = cachedTokens,
                    WebSearchCalls = searchCalls,
                    SearchEvidence = ExtractSearchEvidence(parsed),
                    OutputItemTypes = ExtractOutputItemTypes(parsed),
                    CurrentCost = currentCost,
                    IsSuccess = true
                }
            };
        }

        /// <summary>
        /// The fallback: chat completions with enable_search, for Qwen models the
        /// Responses API will not serve.
        ///
        /// The search really does run server-side here, but Model Studio states
        /// that this endpoint does not return search sources, so there is no tool
        /// trace to count. That matters more than it sounds: everywhere else in
        /// this codebase WebSearchCalls == 0 is the one signal separating a
        /// researched answer from a model answering out of memory over HTTP 200,
        /// and a successful fallback reporting zero would be read as exactly that
        /// failure. So a successful search here reports one call — the honest
        /// claim that a server-side search was requested and the request
        /// succeeded — and OutputItemTypes carries the endpoint name so the
        /// figure is never mistaken for a counted trace. Any search_info the
        /// endpoint does happen to return is preferred over that assumption.
        /// </summary>
        private async Task<SearchAttempt> SearchViaChatAsync(
            EnquiryRequest request,
            string apiModelName,
            int maxTokens,
            decimal inputPricePerMillion,
            decimal outputPricePerMillion,
            string responsesApiError)
        {
            var messages = new List<object>();

            if (!string.IsNullOrWhiteSpace(request.ScrappedData))
            {
                messages.Add(new { role = "system", content = request.ScrappedData });
            }

            messages.Add(new { role = "user", content = request.Prompt });

            var requestBody = new Dictionary<string, object>
            {
                { "model", apiModelName },
                { "messages", messages },
                { "max_tokens", maxTokens },
                { "stream", false },
                { "enable_thinking", false },
                { "enable_search", true },
                {
                    "search_options", new Dictionary<string, object>
                    {
                        // Configured, and "agent" must not be configured here —
                        // it switches the model into thinking mode, which this
                        // non-streaming path cannot use. See QwenSettings.
                        { "search_strategy", _searchStrategy },
                        // Asked for even though the compatibility notes say this
                        // endpoint will not return sources: when it does, the
                        // evidence below is real rather than assumed, and when it
                        // does not the flag costs nothing.
                        { "enable_source", true }
                    }
                }
            };

            using var httpContent = new StringContent(
                JsonConvert.SerializeObject(requestBody),
                Encoding.UTF8,
                "application/json"
            );

            var response = await _httpClient.PostAsync($"{_baseUrl}/chat/completions", httpContent);
            var responseContent = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                // Both failures are reported, not just this one. On its own the
                // chat error reads as the whole story and hides that the
                // Responses API was tried first and said something different —
                // which is usually the more useful half.
                return new SearchAttempt
                {
                    Pitch = new PitchResult
                    {
                        Content = $"Qwen web search failed on both endpoints for model "
                                + $"'{apiModelName}'. Responses API: {responsesApiError} | "
                                + $"Chat completions ({(int)response.StatusCode}): {responseContent}",
                        IsSuccess = false
                    }
                };
            }

            var parsed = JObject.Parse(responseContent);

            string output = parsed["choices"]?[0]?["message"]?["content"]?.ToString() ?? "";
            string finishReason = parsed["choices"]?[0]?["finish_reason"]?.ToString() ?? "";

            int promptTokens = parsed["usage"]?["prompt_tokens"]?.Value<int>() ?? 0;
            int completionTokens = parsed["usage"]?["completion_tokens"]?.Value<int>() ?? 0;
            int totalTokens = parsed["usage"]?["total_tokens"]?.Value<int>()
                              ?? promptTokens + completionTokens;
            int cachedTokens =
                parsed.SelectToken("usage.prompt_tokens_details.cached_tokens")?.Value<int>() ?? 0;

            var evidence = ExtractChatSearchEvidence(parsed);

            bool answered =
                !string.IsNullOrWhiteSpace(output) &&
                !string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase);

            int searchCalls = evidence.Count > 0 ? evidence.Count : (answered ? 1 : 0);

            decimal currentCost =
                TokenCost(promptTokens, completionTokens, inputPricePerMillion, outputPricePerMillion)
                + SearchCost(searchCalls);

            if (evidence.Count == 0 && answered)
            {
                evidence.Add(
                    "search: ran server-side via chat completions enable_search; "
                  + "this endpoint does not itemise sources");
            }

            if (!answered)
            {
                return new SearchAttempt
                {
                    Pitch = new PitchResult
                    {
                        Content = string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase)
                            ? $"Qwen web search truncated: hit max_tokens ({maxTokens:N0}) on the "
                            + "chat-completions search path. Raise MaxTokens for this model in "
                            + "ModelRates, or reduce the batch size."
                            : $"Qwen returned no answer text from the chat-completions search path "
                            + $"(finish_reason: '{finishReason}').",
                        PromptTokens = promptTokens,
                        CompletionTokens = completionTokens,
                        TotalTokens = totalTokens,
                        CachedTokens = cachedTokens,
                        WebSearchCalls = searchCalls,
                        SearchEvidence = evidence,
                        OutputItemTypes = new List<string> { "chat_completions.enable_search" },
                        CurrentCost = currentCost,
                        IsSuccess = false
                    }
                };
            }

            return new SearchAttempt
            {
                Pitch = new PitchResult
                {
                    Content = output,
                    PromptTokens = promptTokens,
                    CompletionTokens = completionTokens,
                    TotalTokens = totalTokens,
                    CachedTokens = cachedTokens,
                    WebSearchCalls = searchCalls,
                    SearchEvidence = evidence,
                    OutputItemTypes = new List<string> { "chat_completions.enable_search" },
                    CurrentCost = currentCost,
                    IsSuccess = true
                }
            };
        }

        /// <summary>
        /// Plain generation with reasoning on, through the Responses API and with
        /// no tools attached. This is the "-thinking" branch of GeneratePitchAsync
        /// and nothing else should call it; the endpoint is only being borrowed
        /// because the chat one refuses enable_thinking on a non-streaming call.
        /// </summary>
        private async Task<PitchResult> ReasoningViaResponsesAsync(
            EnquiryRequest request,
            string apiModelName,
            int maxTokens,
            decimal inputPricePerMillion,
            decimal outputPricePerMillion)
        {
            var requestBody = new Dictionary<string, object>
            {
                { "model", apiModelName },
                { "input", request.Prompt },
                { "max_output_tokens", maxTokens },
                { "reasoning", ReasoningEffort }
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

            var response = await _httpClient.PostAsync($"{_baseUrl}/responses", httpContent);
            var responseContent = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                return new PitchResult
                {
                    Content = $"Qwen API Error ({(int)response.StatusCode}) on thinking model "
                            + $"'{apiModelName}': {responseContent}",
                    IsSuccess = false
                };
            }

            var parsed = JObject.Parse(responseContent);

            string output = parsed["output_text"]?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(output))
                output = ExtractResponsesText(parsed);

            int promptTokens = parsed["usage"]?["input_tokens"]?.Value<int>() ?? 0;
            int completionTokens = parsed["usage"]?["output_tokens"]?.Value<int>() ?? 0;
            int totalTokens = parsed["usage"]?["total_tokens"]?.Value<int>()
                              ?? promptTokens + completionTokens;
            int cachedTokens =
                parsed.SelectToken("usage.input_tokens_details.cached_tokens")?.Value<int>() ?? 0;

            decimal currentCost =
                TokenCost(promptTokens, completionTokens, inputPricePerMillion, outputPricePerMillion);

            if (string.IsNullOrWhiteSpace(output))
            {
                string incompleteReason =
                    parsed.SelectToken("incomplete_details.reason")?.ToString() ?? "";

                return new PitchResult
                {
                    Content = "Qwen returned no answer text in thinking mode"
                            + (incompleteReason.Length > 0 ? $" (reason '{incompleteReason}')" : "")
                            + $": the {maxTokens:N0} token output budget went to reasoning. "
                            + "Raise MaxTokens for this model in ModelRates.",
                    PromptTokens = promptTokens,
                    CompletionTokens = completionTokens,
                    TotalTokens = totalTokens,
                    CachedTokens = cachedTokens,
                    CurrentCost = currentCost,
                    IsSuccess = false
                };
            }

            return new PitchResult
            {
                Content = output,
                PromptTokens = promptTokens,
                CompletionTokens = completionTokens,
                TotalTokens = totalTokens,
                CachedTokens = cachedTokens,
                CurrentCost = currentCost,
                IsSuccess = true
            };
        }

        // =====================================================================
        // Cost
        // =====================================================================

        private static decimal TokenCost(
            int promptTokens,
            int completionTokens,
            decimal inputPricePerMillion,
            decimal outputPricePerMillion) =>
            (promptTokens * inputPricePerMillion / 1_000_000m) +
            (completionTokens * outputPricePerMillion / 1_000_000m);

        /// <summary>
        /// The per-call search fee, which is Qwen's own charge on top of the
        /// tokens. DeepSeek has no equivalent — it bills search entirely as the
        /// extra tokens the search consumes — so this term has no counterpart in
        /// DeepSeekPitchService and must not be dropped by analogy with it.
        /// </summary>
        private decimal SearchCost(int webSearchCalls) =>
            webSearchCalls <= 0
                ? 0m
                : webSearchCalls * _searchCostPerThousandCalls / 1000m;

        // =====================================================================
        // Response parsing
        // =====================================================================

        /// <summary>
        /// How many server-side web searches the model actually ran, counted from
        /// the Responses output items. This is the number that proves a research
        /// answer was researched.
        /// </summary>
        public static int CountWebSearchCalls(JObject parsed)
        {
            // Model Studio reports the count it bills on, under usage. That is
            // the authoritative figure and it is preferred over counting items:
            // the two agreed on every response measured, but only this one is
            // what the invoice is built from, and a search that is billed
            // without emitting its own output item would otherwise go unseen.
            int billed =
                parsed.SelectToken($"usage.x_tools.{WebSearchToolType}.count")?.Value<int>()
                ?? parsed.SelectToken($"usage.x_details[0].plugins.{WebSearchToolType}.count")?.Value<int>()
                ?? -1;

            if (billed >= 0) return billed;

            if (parsed["output"] is not JArray outputs) return 0;

            // Contains, not an exact match on "web_search_call": a suffixed or
            // versioned type name would otherwise report zero searches, which
            // now fails a batch. Reading a variant as a search is the safer of
            // the two errors, and the DeepSeek and OpenAI counters both match
            // this way already.
            return outputs.Count(item =>
                item["type"]?.ToString()?.Contains(WebSearchToolType, StringComparison.OrdinalIgnoreCase) == true);
        }

        /// <summary>
        /// The queries the model ran and the pages it opened, read from the action
        /// on each web_search_call item. A score without its evidence cannot be
        /// defended or re-checked later, and the searching is the expensive half
        /// of producing it.
        /// </summary>
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
        /// Sources from the chat-completions search path, when Model Studio
        /// returns them. It documents search_info.search_results with index,
        /// title and url; the compatibility notes say the OpenAI-compatible
        /// endpoint omits it, so an empty list here is expected rather than
        /// wrong. Both the DashScope shape and the flattened compatible-mode
        /// shape are read, because which one arrives is not worth depending on.
        /// </summary>
        public static List<string> ExtractChatSearchEvidence(JObject parsed)
        {
            var evidence = new List<string>();

            var results =
                parsed.SelectToken("output.search_info.search_results") as JArray
                ?? parsed.SelectToken("search_info.search_results") as JArray
                ?? parsed.SelectToken("choices[0].message.search_info.search_results") as JArray;

            if (results == null) return evidence;

            foreach (var item in results)
            {
                var title = item["title"]?.ToString();
                var url = item["url"]?.ToString();

                if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(url))
                    evidence.Add($"source: {title} ({url})");
                else if (!string.IsNullOrWhiteSpace(url))
                    evidence.Add($"source: {url}");
                else if (!string.IsNullOrWhiteSpace(title))
                    evidence.Add($"source: {title}");
            }

            return evidence;
        }

        /// <summary>
        /// Every distinct item type the response carried. Diagnostic only: when a
        /// research call reports no searches, this is what says whether the model
        /// really skipped the search or whether the search item simply arrived
        /// under a name this code does not recognise.
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

        /// <summary>
        /// Walks the Responses output items when the convenience output_text
        /// field isn't present.
        ///
        /// Only assistant answers count. The same array carries reasoning items,
        /// whose content is the model's private chain of thought, and commentary
        /// it narrates between searches. Both have a text field, so taking every
        /// text field returns pages of prose with the answer buried in it — or,
        /// when the model ran out of budget before answering, pages of prose with
        /// no answer in it at all. Callers expecting JSON then fail on a reply
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
