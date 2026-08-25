using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PitchGenApi.Interfaces;
using PitchGenApi.Model;
using PitchGenApi.Model.DTOs;
using PitchGenApi.Services;
using System.Net.Http.Json;

namespace PitchGenApi.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class ExtensionController : ControllerBase
    {
        private readonly IExtensionRepository _extensionRepository;
        private readonly IExtensionProfileService _extensionProfileService;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _configuration;
        private readonly ContactRepository _contactRepository;
        private readonly IPitchService _pitchService;
        private readonly DeepSeekPitchService _deepSeekService;
        private readonly IAiModelSettingsService _aiModelSettings;

        public ExtensionController(
            IExtensionRepository extensionRepository,
            IExtensionProfileService extensionProfileService,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            ContactRepository contactRepository,
            IPitchService pitchService,
            DeepSeekPitchService deepSeekService,
            IAiModelSettingsService aiModelSettings)
        {
            _extensionRepository = extensionRepository;
            _extensionProfileService = extensionProfileService;
            _httpClientFactory = httpClientFactory;
            _configuration = configuration;
            _contactRepository = contactRepository;
            _pitchService = pitchService;
            _deepSeekService = deepSeekService;
            _aiModelSettings = aiModelSettings;
        }

        [HttpPost("EX_prospeo-unlock")]
        public async Task<IActionResult> UnlockWithProspeo([FromBody] ProspeoUnlockRequestDto request,CancellationToken cancellationToken)
        {
            if (request == null || request.ClientID <= 0 ||
                string.IsNullOrWhiteSpace(request.LinkedInUrl))
            {
                return BadRequest(UnlockEmailResult.Failed(
                    request?.ContactID,
                    "ClientID and LinkedInUrl are required."));
            }

            // The trace below carries the raw prompt and the raw model reply, so
            // it is built for everyone but handed only to an admin.
            var isAdmin = await CallerIsAdminAsync(request.ClientID);
            var diagnostics = new UnlockDiagnostics();
            var totalTimer = System.Diagnostics.Stopwatch.StartNew();

            IActionResult Finish(UnlockEmailResult result, string mode, string reason)
            {
                diagnostics.Mode = mode;
                diagnostics.ModeReason = reason;
                diagnostics.ElapsedMs = (int)totalTimer.ElapsedMilliseconds;
                result.Diagnostics = isAdmin ? diagnostics : null;
                return Ok(result);
            }

            void Stage(string name, string outcome, string detail, long elapsedMs) =>
                diagnostics.Stages.Add(new UnlockStageDiagnostics
                {
                    Name = name,
                    Outcome = outcome,
                    Detail = detail,
                    ElapsedMs = (int)elapsedMs
                });

            if (!await _contactRepository.HasAvailableCreditAsync(request.ClientID))
            {
                Stage("credit", "error", "No unlock credit available.", 0);
                return Finish(
                    UnlockEmailResult.Failed(
                        request.ContactID,
                        "No unlock credit is available. Please buy credits to unlock this email."),
                    "none",
                    "Stopped before any lookup: the client has no unlock credit.");
            }

            // ------------------------------------------------------- mode 1: cache
            var cacheTimer = System.Diagnostics.Stopwatch.StartNew();
            var cachedEmail = await _extensionRepository.GetProspeoUnlockedEmailAsync(
                request.LinkedInUrl);
            cacheTimer.Stop();

            if (!string.IsNullOrWhiteSpace(cachedEmail))
            {
                Stage("cache", "hit",
                    "This LinkedIn URL was unlocked in the last 30 days; no external call was made.",
                    cacheTimer.ElapsedMilliseconds);

                var cachedCompleted = await _extensionRepository.CompleteProspeoUnlockAsync(
                    request.ContactID,
                    request.ClientID,
                    request.LinkedInUrl,
                    cachedEmail);

                return Finish(
                    cachedCompleted
                        ? UnlockEmailResult.Succeeded(
                            request.ContactID,
                            cachedEmail,
                            "Email reused from the 30-day unlock cache and one credit deducted.",
                            "cache")
                        : UnlockEmailResult.Failed(
                            request.ContactID,
                            "No unlock credit is available. Please buy credits to unlock this email."),
                    "cache",
                    "Served from the 30-day unlock cache. Prospeo and the AI fallback were never called.");
            }

            Stage("cache", "miss",
                "No unlock for this LinkedIn URL in the last 30 days.",
                cacheTimer.ElapsedMilliseconds);

            // ----------------------------------------------------- mode 2: Prospeo
            var apiKey = _configuration["Prospeo:ApiKey"];
            var prospeo = new UnlockProspeoDiagnostics
            {
                ApiKeyConfigured = !string.IsNullOrWhiteSpace(apiKey),
                Endpoint = "https://api.prospeo.io/enrich-person"
            };
            diagnostics.Prospeo = prospeo;

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                prospeo.RejectedBecause = "No Prospeo:ApiKey is configured.";
                Stage("prospeo", "skipped", prospeo.RejectedBecause, 0);
                return Finish(
                    await CompleteAiFallbackUnlockAsync(request, diagnostics),
                    "ai",
                    "Prospeo was skipped because no API key is configured, so the AI fallback ran.");
            }

            var prospeoTimer = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                var prospeoBody = new
                {
                    only_verified_email = true,
                    enrich_mobile = false,
                    data = new { linkedin_url = request.LinkedInUrl.Trim() }
                };
                prospeo.RequestBody = JsonConvert.SerializeObject(prospeoBody, Formatting.Indented);

                using var httpRequest = new HttpRequestMessage(
                    HttpMethod.Post,
                    prospeo.Endpoint);
                httpRequest.Headers.Add("X-KEY", apiKey);
                httpRequest.Content = JsonContent.Create(prospeoBody);

                var client = _httpClientFactory.CreateClient();
                using var response = await client.SendAsync(httpRequest, cancellationToken);
                prospeo.HttpStatus = (int)response.StatusCode;

                // Read the body as text first so the raw payload survives into the
                // trace even when it fails to deserialise.
                var rawBody = await response.Content.ReadAsStringAsync(cancellationToken);
                prospeo.RawResponse = rawBody;

                if (!response.IsSuccessStatusCode)
                {
                    prospeo.RejectedBecause =
                        $"Prospeo replied {(int)response.StatusCode} {response.StatusCode}.";
                    prospeoTimer.Stop();
                    Stage("prospeo", "error", prospeo.RejectedBecause,
                        prospeoTimer.ElapsedMilliseconds);
                    return Finish(
                        await CompleteAiFallbackUnlockAsync(request, diagnostics),
                        "ai",
                        "Prospeo returned an error status, so the AI fallback ran.");
                }

                ProspeoEnrichResponseDto? result = null;
                try
                {
                    result = System.Text.Json.JsonSerializer
                        .Deserialize<ProspeoEnrichResponseDto>(rawBody);
                }
                catch (System.Text.Json.JsonException ex)
                {
                    prospeo.RejectedBecause = "Prospeo returned unreadable JSON: " + ex.Message;
                }

                var emailResult = result?.Person?.Email;
                var email = emailResult?.Email?.Trim();
                prospeo.Revealed = emailResult?.Revealed;
                prospeo.EmailStatus = emailResult?.Status;

                if (prospeo.RejectedBecause == null)
                {
                    prospeo.RejectedBecause =
                        result?.Error == true
                            ? "Prospeo flagged the response as an error."
                        : emailResult == null
                            ? "Prospeo returned no email object for this profile."
                        : !emailResult.Revealed
                            ? "Prospeo did not reveal the address."
                        : !string.Equals(emailResult.Status, "VERIFIED", StringComparison.OrdinalIgnoreCase)
                            ? "Prospeo status was '" + emailResult.Status + "', not VERIFIED."
                        : string.IsNullOrWhiteSpace(email)
                            ? "Prospeo returned an empty address."
                        : null;
                }

                if (prospeo.RejectedBecause != null)
                {
                    prospeoTimer.Stop();
                    Stage("prospeo", "miss", prospeo.RejectedBecause,
                        prospeoTimer.ElapsedMilliseconds);
                    return Finish(
                        await CompleteAiFallbackUnlockAsync(request, diagnostics),
                        "ai",
                        "Prospeo had no verified address, so the AI fallback ran.");
                }

                prospeoTimer.Stop();
                Stage("prospeo", "hit", "Prospeo returned a verified address.",
                    prospeoTimer.ElapsedMilliseconds);

                var completed = await _extensionRepository.CompleteProspeoUnlockAsync(
                    request.ContactID,
                    request.ClientID,
                    request.LinkedInUrl,
                    email);

                if (!completed)
                {
                    return Finish(
                        UnlockEmailResult.Failed(
                            request.ContactID,
                            "No unlock credit is available. Please buy credits to unlock this email."),
                        "prospeo",
                        "Prospeo found the address but the credit could not be deducted.");
                }

                return Finish(
                    UnlockEmailResult.Succeeded(
                        request.ContactID,
                        email,
                        "Verified email unlocked and one credit deducted."),
                    "prospeo",
                    "Prospeo returned a verified address, so the AI fallback never ran.");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                prospeoTimer.Stop();
                prospeo.RejectedBecause = "The Prospeo call timed out.";
                Stage("prospeo", "error", prospeo.RejectedBecause,
                    prospeoTimer.ElapsedMilliseconds);
                return Finish(
                    await CompleteAiFallbackUnlockAsync(request, diagnostics),
                    "ai",
                    "The Prospeo call timed out, so the AI fallback ran.");
            }
            catch (HttpRequestException ex)
            {
                prospeoTimer.Stop();
                prospeo.RejectedBecause = "The Prospeo call failed: " + ex.Message;
                Stage("prospeo", "error", prospeo.RejectedBecause,
                    prospeoTimer.ElapsedMilliseconds);
                return Finish(
                    await CompleteAiFallbackUnlockAsync(request, diagnostics),
                    "ai",
                    "The Prospeo call could not be made, so the AI fallback ran.");
            }
        }

        /// <summary>
        /// Whether the caller may see an unlock trace. The token is shared with
        /// the web app and carries no admin claim, so this reads the flag from
        /// the database - and only after the Bearer token proves the caller is
        /// the client whose id is in the body, since this endpoint otherwise
        /// takes that id on trust.
        /// </summary>
        /// <summary>
        /// Re-parses a Newtonsoft array through System.Text.Json so the response
        /// serialiser renders it as a real array rather than JToken internals.
        /// </summary>
        private static System.Text.Json.Nodes.JsonNode? ToJsonNode(JArray? value)
        {
            if (value == null)
                return null;

            try
            {
                return System.Text.Json.Nodes.JsonNode.Parse(value.ToString(Formatting.None));
            }
            catch (System.Text.Json.JsonException)
            {
                return null;
            }
        }

        private async Task<bool> CallerIsAdminAsync(int clientId)
        {
            if (clientId <= 0 || User?.Identity?.IsAuthenticated != true)
                return false;

            var tokenClientId = User.FindFirst("UserId")?.Value;

            if (!int.TryParse(tokenClientId, out var parsed) || parsed != clientId)
                return false;

            return await _contactRepository.IsAdminAsync(clientId);
        }

        [HttpPost]
        public async Task<IActionResult> GetUnlockedEmail (GetUnlockedEmailRequest request, CancellationToken cancellationToken)
        {
            return Ok(await UnlockAsync(request, cancellationToken));
        }

        [HttpPost("multiple")]
        [HttpPost("GetMulitpleUnlockResults")]
        public async Task<IActionResult> GetMultipleUnlockResults( List<GetUnlockedEmailRequest> requests, CancellationToken cancellationToken)
        {
            if (requests == null || requests.Count == 0)
                return BadRequest(new { success = false, message = "At least one contact is required." });

            var results = new List<UnlockEmailResult>(requests.Count);
            foreach (var request in requests)
            {
                cancellationToken.ThrowIfCancellationRequested();
                results.Add(await UnlockAsync(request, cancellationToken));
            }

            return Ok(new { data = results });
        }

        [HttpPost("EX_match-contact")]
        public async Task<IActionResult> MatchContact([FromBody] ContactMatchRequestDto request)
        {
            if (request.ClientId <= 0)
                return BadRequest(new { message = "A valid ClientId is required." });

            if (string.IsNullOrWhiteSpace(request.LinkedInUrl))
                return BadRequest(new { message = "LinkedInUrl is required." });

            var result = await _extensionRepository.MatchContactAsync(request);
            return StatusCode(result.StatusCode, result.Body);
        }

        [HttpPost("EX_add-contact-to-datafile")]
        public async Task<IActionResult> AddContactToDataFile([FromBody] AddContactToDataFileRequestDto request)
        {
            if (request.ClientId <= 0)
                return BadRequest(new { message = "A valid ClientId is required." });

            if (request.DataFileId <= 0)
                return BadRequest(new { message = "A valid DataFileId is required." });

            if (string.IsNullOrWhiteSpace(request.LinkedInUrl))
                return BadRequest(new { message = "LinkedInUrl is required." });

            if (string.IsNullOrWhiteSpace(request.Email))
                return BadRequest(new { message = "Email is required." });

            var result = await _extensionRepository.AddContactToDataFileAsync(request);
            return StatusCode(result.StatusCode, result.Body);
        }

        [HttpPost("EX_update-contact-fields")]
        public async Task<IActionResult> UpdateContactFields([FromBody] UpdateContactFieldsRequestDto request)
        {
            if (request.ClientId <= 0 || request.DataFileId <= 0 || request.ContactId <= 0)
            {
                return BadRequest(new
                {
                    message = "Valid ClientId, DataFileId and ContactId are required."
                });
            }

            var result = await _extensionRepository.UpdateContactFieldsAsync(request);
            return StatusCode(result.StatusCode, result.Body);
        }

        /// <summary>
        /// One call for the extension panel on open: does this LinkedIn URL exist
        /// in any of the client's lists, and what lists are available to save into.
        /// </summary>
        [HttpPost("EX_profile-context")]
        public async Task<IActionResult> GetProfileContext(
            [FromBody] ExtensionProfileContextRequestDto request)
        {
            if (request == null || request.ClientId <= 0)
                return BadRequest(new { message = "A valid ClientId is required." });

            if (string.IsNullOrWhiteSpace(request.LinkedInUrl))
                return BadRequest(new { message = "LinkedInUrl is required." });

            var result = await _extensionProfileService.GetProfileContextAsync(request);
            return StatusCode(result.StatusCode, result.Body);
        }

        /// <summary>
        /// Creates the contact in the chosen list, or patches the fields the user
        /// ticked on a contact that already exists.
        /// </summary>
        [HttpPost("EX_save-profile")]
        public async Task<IActionResult> SaveProfile(
            [FromBody] ExtensionSaveProfileRequestDto request)
        {
            if (request == null || request.ClientId <= 0)
                return BadRequest(new { message = "A valid ClientId is required." });

            var result = await _extensionProfileService.SaveProfileAsync(request);
            return StatusCode(result.StatusCode, result.Body);
        }

        /// <summary>
        /// Summarises the scraped LinkedIn profile with the AI model an admin
        /// picked for the "profile_summary" purpose (Settings &gt; AI models) and
        /// stores it in the contact's LinkedIn information field.
        ///
        /// Like find-email it runs the shared web-search path, so DeepSeek and
        /// OpenAI models both work, and the search costs the client one credit.
        /// </summary>
        [HttpPost("EX_profile-summary")]
        public async Task<IActionResult> GenerateProfileSummary(
            [FromBody] ExtensionProfileSummaryRequestDto request,
            CancellationToken cancellationToken)
        {
            if (request == null || request.ClientId <= 0)
                return BadRequest(new { message = "A valid ClientId is required." });

            var result = await _extensionProfileService.GenerateProfileSummaryAsync(
                request,
                cancellationToken);
            return StatusCode(result.StatusCode, result.Body);
        }

        /// <summary>
        /// Researches a person's professional email address with the AI model an
        /// admin picked for the "find_email" purpose (Settings &gt; AI models),
        /// running the same web-search call the research step uses.
        ///
        /// Every identifying field is optional: whatever is missing is passed to
        /// the model as "Not provided", so a request with only a name and a
        /// company domain still works.
        ///
        /// The search is billed: one credit is deducted from the client. The
        /// client comes from the authenticated token when it carries a UserId
        /// claim, otherwise from ClientId in the body; when both are present they
        /// have to match.
        /// </summary>
        [HttpPost("find-email-AI")]
        public async Task<IActionResult> FindEmailWithAi(
            [FromBody] FindEmailAiRequestDto request)
        {
            try
            {
                if (request == null)
                    return BadRequest(new { Success = false, Message = "Request body is required." });

                var authenticatedClientId =
                    int.TryParse(User.FindFirst("UserId")?.Value, out var claimClientId) &&
                    claimClientId > 0
                        ? claimClientId
                        : (int?)null;

                // A caller may not pretend to be another client.
                if (authenticatedClientId.HasValue &&
                    request.ClientId > 0 &&
                    request.ClientId != authenticatedClientId.Value)
                {
                    return Forbid();
                }

                var clientId = authenticatedClientId ?? request.ClientId;

                if (clientId <= 0)
                    return BadRequest(new { Success = false, Message = "A valid ClientId is required." });

                // Fail before spending anything on the model when the client has
                // no credit left to pay for the search.
                if (!await _contactRepository.HasAvailableCreditAsync(clientId))
                {
                    return Ok(new
                    {
                        Success = false,
                        Message = "No credit is available. Please buy credits to run an AI email search."
                    });
                }

                // Nothing is individually compulsory, but an entirely empty
                // request gives the model nothing to search for.
                bool hasAnyInput =
                    !string.IsNullOrWhiteSpace(request.FullName) ||
                    !string.IsNullOrWhiteSpace(request.JobTitle) ||
                    !string.IsNullOrWhiteSpace(request.Company) ||
                    !string.IsNullOrWhiteSpace(request.Location) ||
                    !string.IsNullOrWhiteSpace(request.ProfileUrl) ||
                    !string.IsNullOrWhiteSpace(request.CompanyUrl);

                if (!hasAnyInput)
                {
                    return BadRequest(new
                    {
                        Success = false,
                        Message = "At least one of FullName, JobTitle, Company, Location, ProfileUrl or CompanyUrl is required."
                    });
                }

                var aiSearch = await FindEmailWithAiCoreAsync(request, clientId);
                var searchResult = aiSearch.SearchResult;
                var modelName = aiSearch.ModelName;
                var finalPrompt = aiSearch.FinalPrompt;

                if (!searchResult.IsSuccess)
                {
                    return StatusCode(StatusCodes.Status502BadGateway, new
                    {
                        Success = false,
                        Message = "The email research call failed.",
                        Model = modelName,
                        Error = searchResult.Content
                    });
                }

                var raw = searchResult.Content ?? "";

                return Ok(new
                {
                    Success = true,
                    ClientId = clientId,
                    Model = modelName,
                    Provider = IsDeepSeekModel(modelName) ? "DeepSeek" : "OpenAI",
                    Results = aiSearch.Results,
                    Raw = raw,
                    FinalPrompt = finalPrompt,
                    Usage = new
                    {
                        searchResult.PromptTokens,
                        searchResult.CompletionTokens,
                        searchResult.SearchTokens,
                        searchResult.TotalTokens,
                        searchResult.CurrentCost
                    }
                });
            }
            catch (TaskCanceledException ex)
            {
                return StatusCode(StatusCodes.Status504GatewayTimeout, new
                {
                    Success = false,
                    Message = "The email research call timed out.",
                    Error = ex.Message
                });
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, new
                {
                    Success = false,
                    Message = "Internal server error.",
                    Error = ex.Message
                });
            }
        }

        //------------------------------------------------------------------------Private Mathods---------------------------------------------------------------------------------

        private async Task<AiEmailSearchOutcome> FindEmailWithAiCoreAsync(
            FindEmailAiRequestDto request,
            int billingClientId)
        {
            var finalPrompt = FindEmailPrompt.Build(
                request.FullName,
                request.JobTitle,
                request.Company,
                request.Location,
                request.ProfileUrl,
                request.CompanyUrl);
            var modelName = await _aiModelSettings.GetModelAsync(AiModelPurposes.FindEmail);
            var enquiryRequest = new EnquiryRequest
            {
                Prompt = finalPrompt,
                ScrappedData = "",
                ModelName = modelName
            };
            var searchResult = IsDeepSeekModel(modelName)
                ? await _deepSeekService.GenerateWebSearchAsync(enquiryRequest, billingClientId)
                : await _pitchService.GenerateWebSearchAsync(enquiryRequest, billingClientId);
            var results = searchResult.IsSuccess
                ? ParseFindEmailResults(searchResult.Content ?? "")
                : new JArray();

            return new AiEmailSearchOutcome(
                searchResult,
                modelName,
                finalPrompt,
                results);
        }

        /// <summary>
        /// Mode 3. Fills in <paramref name="diagnostics"/> as it goes so an admin
        /// can see the prompt, the model's raw reply and which candidate won.
        /// </summary>
        private async Task<UnlockEmailResult> CompleteAiFallbackUnlockAsync(
            ProspeoUnlockRequestDto request,
            UnlockDiagnostics diagnostics)
        {
            var aiRequest = new FindEmailAiRequestDto
            {
                ClientId = request.ClientID,
                FullName = request.Name,
                JobTitle = request.JobTitle,
                Company = request.CompanyName,
                Location = request.Location,
                ProfileUrl = request.LinkedInUrl,
                CompanyUrl = request.CompanyUrl ?? request.Domain
            };

            var aiTimer = System.Diagnostics.Stopwatch.StartNew();

            // Zero prevents the model service from deducting. Completion below
            // atomically deducts once and writes UnlockedContacts only after a
            // usable email has actually been returned.
            var aiSearch = await FindEmailWithAiCoreAsync(aiRequest, 0);
            aiTimer.Stop();

            var ai = new UnlockAiDiagnostics
            {
                Provider = IsDeepSeekModel(aiSearch.ModelName) ? "DeepSeek" : "OpenAI",
                Model = aiSearch.ModelName ?? "",
                Prompt = aiSearch.FinalPrompt ?? "",
                Raw = aiSearch.SearchResult?.Content ?? "",
                Results = ToJsonNode(aiSearch.Results),
                IsSuccess = aiSearch.SearchResult?.IsSuccess ?? false,
                Usage = aiSearch.SearchResult == null ? null : new UnlockAiUsage
                {
                    PromptTokens = aiSearch.SearchResult.PromptTokens,
                    CompletionTokens = aiSearch.SearchResult.CompletionTokens,
                    SearchTokens = aiSearch.SearchResult.SearchTokens,
                    TotalTokens = aiSearch.SearchResult.TotalTokens,
                    CurrentCost = aiSearch.SearchResult.CurrentCost
                }
            };
            diagnostics.Ai = ai;

            void Stage(string outcome, string detail) =>
                diagnostics.Stages.Add(new UnlockStageDiagnostics
                {
                    Name = "ai",
                    Outcome = outcome,
                    Detail = detail,
                    ElapsedMs = (int)aiTimer.ElapsedMilliseconds
                });

            if (!aiSearch.SearchResult.IsSuccess)
            {
                ai.ChoiceReason = "The model call itself failed.";
                Stage("error", "The " + ai.Provider + " call failed (" + ai.Model + ").");

                return UnlockEmailResult.Failed(
                    request.ContactID,
                    "Prospeo found no verified email and the AI fallback failed.");
            }

            // A direct address beats a guessed pattern; after that the model's own
            // confidence decides, and ties keep the order the model returned.
            var ranked = aiSearch.Results
                .OfType<JObject>()
                .Select((item, index) => new
                {
                    Email = item["email"]?.Value<string>()?.Trim(),
                    Type = item["type"]?.Value<string>() ?? "",
                    Confidence = item["confidence"]?.Value<int>() ?? 0,
                    Index = index
                })
                .Where(item => !string.IsNullOrWhiteSpace(item.Email) &&
                    System.Net.Mail.MailAddress.TryCreate(item.Email, out _))
                .OrderByDescending(item =>
                    string.Equals(item.Type, "direct", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(item => item.Confidence)
                .ThenBy(item => item.Index)
                .ToList();

            var chosen = ranked.FirstOrDefault();
            var email = chosen?.Email;

            if (string.IsNullOrWhiteSpace(email))
            {
                ai.ChoiceReason = aiSearch.Results.Count == 0
                    ? "The model returned no candidates."
                    : "The model returned " + aiSearch.Results.Count +
                        " candidate(s), none of them a valid address.";
                Stage("miss", ai.ChoiceReason);

                return UnlockEmailResult.Failed(
                    request.ContactID,
                    "No email address was found by Prospeo or the AI fallback. No credit was deducted.");
            }

            ai.ChosenEmail = email;
            ai.ChoiceReason = "Picked from " + ranked.Count + " valid candidate(s): type '" +
                chosen.Type + "', confidence " + chosen.Confidence + ".";
            Stage("hit", ai.ChoiceReason);

            var completed = await _extensionRepository.CompleteProspeoUnlockAsync(
                request.ContactID,
                request.ClientID,
                request.LinkedInUrl,
                email);

            return completed
                ? UnlockEmailResult.Succeeded(
                    request.ContactID,
                    email,
                    "Email found by AI fallback, unlock history saved and one credit deducted.",
                    "ai")
                : UnlockEmailResult.Failed(
                    request.ContactID,
                    "Email was found by AI, but unlock could not complete because credit was unavailable.");
        }

        private sealed record AiEmailSearchOutcome(
            PitchResult SearchResult,
            string ModelName,
            string FinalPrompt,
            JArray Results);

        private static bool IsDeepSeekModel(string? modelName)
            => modelName?.StartsWith("deepseek-", StringComparison.OrdinalIgnoreCase) == true;

        /// <summary>
        /// The instruction asks for bare JSON, but models still wrap it in a
        /// ```json fence or add a sentence around it, so pull out the object and
        /// return its "results" array. Returns an empty array when the answer
        /// cannot be parsed — the raw text is always returned alongside.
        /// </summary>
        private static JArray ParseFindEmailResults(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return new JArray();

            var text = content.Trim();

            // Strip a leading ```json / ``` fence and its closing fence.
            if (text.StartsWith("```", StringComparison.Ordinal))
            {
                int firstLineBreak = text.IndexOf('\n');
                if (firstLineBreak >= 0)
                    text = text[(firstLineBreak + 1)..];

                int closingFence = text.LastIndexOf("```", StringComparison.Ordinal);
                if (closingFence >= 0)
                    text = text[..closingFence];

                text = text.Trim();
            }

            // Fall back to the outermost { ... } when prose surrounds the JSON.
            if (!text.StartsWith("{", StringComparison.Ordinal))
            {
                int start = text.IndexOf('{');
                int end = text.LastIndexOf('}');
                if (start < 0 || end <= start)
                    return new JArray();

                text = text[start..(end + 1)];
            }

            try
            {
                var parsed = JsonConvert.DeserializeObject<JObject>(text);
                return parsed?["results"] as JArray ?? new JArray();
            }
            catch (JsonException)
            {
                return new JArray();
            }
        }


        private async Task<UnlockEmailResult> UnlockAsync( GetUnlockedEmailRequest request, CancellationToken cancellationToken)
        {
            if (request == null ||
                request.ClientID <= 0 ||
                string.IsNullOrWhiteSpace(request.Name) ||
                string.IsNullOrWhiteSpace(request.Domain))
            {
                return UnlockEmailResult.Failed(
                    request?.ContactID,
                    "ClientID, Name and Domain are required.");
            }

            var normalizedDomain = NormalizeDomain(request.Domain);
            if (string.IsNullOrWhiteSpace(normalizedDomain))
            {
                return UnlockEmailResult.Failed(
                    request.ContactID,
                    "Domain must be a valid domain name or website URL.");
            }

            request.Domain = normalizedDomain;

            var status = new List<string>
            {
                $"{DateTime.UtcNow:O} Checking whether the contact was unlocked within 30 days."
            };

            var email = await _extensionRepository.GetUnlockedEmailAsync(
                request.Domain,
                request.LinkedInUrl);

            if (!string.IsNullOrWhiteSpace(email))
            {
                status.Add("Contact was unlocked within 30 days; pattern generation was skipped.");
                var validation = await _extensionRepository.Stage2Async(email, cancellationToken);
                status.Add(validation.Status);

                if (validation.State != EmailVerificationState.Valid)
                    return UnlockEmailResult.Failed(request.ContactID, string.Join("\n", status));

                return await CompleteUnlockAsync(request, email, status);
            }

            status.Add("Contact was not unlocked within 30 days.");
            var savedPatterns = await _extensionRepository.GetEmailPatternsAsync(request.Domain);
            var search = await FindValidEmailAsync(
                request, savedPatterns, status, requireExactlyOneStage3Result: false, cancellationToken);

            if (search.VerificationUnavailable)
                return UnlockEmailResult.Failed(request.ContactID, string.Join("\n", status));

            email = search.Email;
            if (string.IsNullOrWhiteSpace(email))
            {
                status.Add("No saved domain pattern succeeded; trying all predefined patterns.");
                search = await FindValidEmailAsync(
                    request,
                    _extensionRepository.GetAllEmailPatterns(),
                    status,
                    requireExactlyOneStage3Result: true,
                    cancellationToken);

                if (search.VerificationUnavailable)
                    return UnlockEmailResult.Failed(request.ContactID, string.Join("\n", status));

                email = search.Email;
            }

            if (string.IsNullOrWhiteSpace(email))
                return UnlockEmailResult.Failed(request.ContactID, string.Join("\n", status));

            return await CompleteUnlockAsync(request, email, status);
        }

        private async Task<EmailSearchResult> FindValidEmailAsync( GetUnlockedEmailRequest request,  IEnumerable<string> patterns, List<string> status, bool requireExactlyOneStage3Result, CancellationToken cancellationToken)
        {
            var generatedEmails = patterns
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(pattern => _extensionRepository.GenerateEmail(
                    request.Name,
                    request.Domain,
                    pattern))
                .Where(email => !string.IsNullOrWhiteSpace(email))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            status.Add($"Generated {generatedEmails.Count} distinct email candidates.");
            var stage2PassedEmails = new List<string>();
            bool stage2Unavailable = false;

            foreach (var generatedEmail in generatedEmails)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var validation = await _extensionRepository.Stage2Async(
                    generatedEmail,
                    cancellationToken);
                Console.WriteLine($"[EmailUnlock] GeneratedEmail={generatedEmail}");
                Console.WriteLine($"[EmailUnlock] VerificationState={validation.State}");
                Console.WriteLine($"[EmailUnlock] VerificationStatus={validation.Status}");
                status.Add($"{generatedEmail}: {validation.Status.Trim()}");

                if (validation.State == EmailVerificationState.Valid)
                    stage2PassedEmails.Add(generatedEmail);
                else if (validation.State == EmailVerificationState.VerificationUnavailable)
                    stage2Unavailable = true;
            }

            status.Add($"Stage 2 accepted {stage2PassedEmails.Count} candidate(s).");

            if (stage2PassedEmails.Count > 2)
            {
                status.Add("More than two candidates were accepted by Stage 2; the domain appears catch-all, so Stage 3 was skipped.");
                return new EmailSearchResult(null, false);
            }

            if (stage2PassedEmails.Count == 0)
                return new EmailSearchResult(null, stage2Unavailable);

            var firstName = request.Name
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault() ?? request.Name;
            var stage3PassedEmails = new List<string>();

            foreach (var stage2Email in stage2PassedEmails)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var stage3 = await _extensionRepository.Stage3Async(
                    stage2Email,
                    firstName,
                    request.ContactID,
                    request.ClientID,
                    cancellationToken);
                status.Add($"{stage2Email}: {stage3.Status.Trim()}");
                Console.WriteLine($"[EmailUnlock] Stage3Email={stage2Email}");
                Console.WriteLine($"[EmailUnlock] Stage3State={stage3.State}");
                Console.WriteLine($"[EmailUnlock] Stage3Status={stage3.Status}");

                if (stage3.State == EmailVerificationState.Valid)
                    stage3PassedEmails.Add(stage2Email);
                else if (stage3.State == EmailVerificationState.VerificationUnavailable)
                    return new EmailSearchResult(null, true);
            }

            status.Add($"Stage 3 accepted {stage3PassedEmails.Count} candidate(s).");
            if (requireExactlyOneStage3Result && stage3PassedEmails.Count != 1)
            {
                status.Add("Predefined-pattern search requires exactly one Stage 3 result.");
                return new EmailSearchResult(null, false);
            }

            return new EmailSearchResult(stage3PassedEmails.FirstOrDefault(), false);
        }

        private async Task<UnlockEmailResult> CompleteUnlockAsync(GetUnlockedEmailRequest request, string email, List<string> status)
        {
            var completed = await _extensionRepository.CompleteUnlockAsync(
                request.ContactID,
                request.ClientID,
                request.LinkedInUrl,
                email,
                request.Name,
                request.Domain);

            if (!completed)
            {
                status.Add("Email was found, but the unlock could not be completed because credit was unavailable.");
                return UnlockEmailResult.Failed(request.ContactID, string.Join("\n", status));
            }

            status.Add("Email found, unlock history saved and one credit deducted.");
            return UnlockEmailResult.Succeeded(request.ContactID, email, string.Join("\n", status));
        }

        private sealed record EmailSearchResult(string? Email, bool VerificationUnavailable);

        private static string? NormalizeDomain(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            string input = value.Trim();
            if (!input.Contains("://", StringComparison.Ordinal))
                input = "https://" + input;

            if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) ||
                string.IsNullOrWhiteSpace(uri.Host))
            {
                return null;
            }

            string host = uri.IdnHost.Trim().TrimEnd('.').ToLowerInvariant();
            if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
                host = host[4..];

            return host.Contains('.') ? host : null;
        }



    }
}
