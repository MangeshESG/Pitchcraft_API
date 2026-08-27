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
        private readonly IHunterEmailService _hunterService;

        public ExtensionController(
            IExtensionRepository extensionRepository,
            IExtensionProfileService extensionProfileService,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            ContactRepository contactRepository,
            IPitchService pitchService,
            DeepSeekPitchService deepSeekService,
            IAiModelSettingsService aiModelSettings,
            IHunterEmailService hunterService)
        {
            _extensionRepository = extensionRepository;
            _extensionProfileService = extensionProfileService;
            _httpClientFactory = httpClientFactory;
            _configuration = configuration;
            _contactRepository = contactRepository;
            _pitchService = pitchService;
            _deepSeekService = deepSeekService;
            _aiModelSettings = aiModelSettings;
            _hunterService = hunterService;
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

            // The address the cache was holding when a forced refresh came in.
            // Reported back so the extension can show what was replaced, and kept
            // out of the AI fallback's candidates so "try again" cannot hand back
            // the same wrong answer. Null on a normal unlock.
            string? rejectedEmail = null;

            IActionResult Finish(UnlockEmailResult result, string mode, string reason)
            {
                // A later stage can name the mode itself - the AI fallback does
                // when Hunter's address beats the model's - and the caller here
                // cannot know that happened. Only fill in the mode it passed
                // when nothing downstream has claimed one.
                if (string.IsNullOrEmpty(diagnostics.Mode))
                {
                    diagnostics.Mode = mode;
                    diagnostics.ModeReason = reason;
                }

                diagnostics.ElapsedMs = (int)totalTimer.ElapsedMilliseconds;
                result.Diagnostics = isAdmin ? diagnostics : null;
                result.PreviousEmail = rejectedEmail;
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

            if (request.ForceRefresh)
            {
                // The caller ticked "look it up again from other sources", so the
                // cached address is read but never served - Prospeo and then the
                // AI fallback answer instead, and whatever they return overwrites
                // the cache row.
                rejectedEmail = string.IsNullOrWhiteSpace(cachedEmail)
                    ? null
                    : cachedEmail.Trim();

                Stage("cache", "skipped",
                    rejectedEmail == null
                        ? "A fresh lookup was requested; the cache held nothing for this URL anyway."
                        : "A fresh lookup was requested, so the cached address '" +
                          rejectedEmail + "' was not served.",
                    cacheTimer.ElapsedMilliseconds);
            }
            else if (!string.IsNullOrWhiteSpace(cachedEmail))
            {
                Stage("cache", "hit",
                    "This LinkedIn URL was unlocked in the last 30 days; no external call was made.",
                    cacheTimer.ElapsedMilliseconds);

                var cachedCompleted = await _extensionRepository.CompleteProspeoUnlockAsync(
                    request.ContactID,
                    request.ClientID,
                    request.LinkedInUrl,
                    cachedEmail);

                var cachedResult = cachedCompleted
                    ? UnlockEmailResult.Succeeded(
                        request.ContactID,
                        cachedEmail,
                        "Email reused from the 30-day unlock cache and one credit deducted.",
                        "cache")
                    : UnlockEmailResult.Failed(
                        request.ContactID,
                        "No unlock credit is available. Please buy credits to unlock this email.");

                // Only a cache answer is worth retrying: Prospeo and the AI
                // fallback have already been asked everything they know.
                cachedResult.CanRetryFromOtherSources = cachedCompleted;

                return Finish(cachedResult,
                    "cache",
                    "Served from the 30-day unlock cache. Prospeo and the AI fallback were never called.");
            }
            else
            {
                Stage("cache", "miss",
                    "No unlock for this LinkedIn URL in the last 30 days.",
                    cacheTimer.ElapsedMilliseconds);
            }

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
                    await CompleteAiFallbackUnlockAsync(request, diagnostics, rejectedEmail),
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
                        await CompleteAiFallbackUnlockAsync(request, diagnostics, rejectedEmail),
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
                        await CompleteAiFallbackUnlockAsync(request, diagnostics, rejectedEmail),
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

                var sameAsRejected = rejectedEmail != null &&
                    string.Equals(email, rejectedEmail, StringComparison.OrdinalIgnoreCase);

                return Finish(
                    UnlockEmailResult.Succeeded(
                        request.ContactID,
                        email,
                        sameAsRejected
                            ? "Prospeo re-verified the same address that was cached, and one credit was deducted."
                            : "Verified email unlocked and one credit deducted."),
                    "prospeo",
                    sameAsRejected
                        ? "Prospeo was asked again and returned the same verified address the cache held."
                        : "Prospeo returned a verified address, so the AI fallback never ran.");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                prospeoTimer.Stop();
                prospeo.RejectedBecause = "The Prospeo call timed out.";
                Stage("prospeo", "error", prospeo.RejectedBecause,
                    prospeoTimer.ElapsedMilliseconds);
                return Finish(
                    await CompleteAiFallbackUnlockAsync(request, diagnostics, rejectedEmail),
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
                    await CompleteAiFallbackUnlockAsync(request, diagnostics, rejectedEmail),
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

                // The same escalation the unlock flow uses: when the model's best
                // candidate falls short of the threshold, Hunter is asked as well.
                // Its answer is reported alongside the model's rather than mixed
                // into Results, so the caller can see where each address came from.
                var bestCandidate = aiSearch.Results
                    .OfType<JObject>()
                    .Select(item => new
                    {
                        Email = item["email"]?.Value<string>()?.Trim(),
                        Confidence = item["confidence"]?.Value<int>() ?? 0
                    })
                    .Where(item => !string.IsNullOrWhiteSpace(item.Email))
                    .OrderByDescending(item => item.Confidence)
                    .FirstOrDefault();

                var threshold = _hunterService.ConfidenceThreshold;
                object? hunter = null;

                if (bestCandidate == null || bestCandidate.Confidence < threshold)
                {
                    var lookup = await _hunterService.FindEmailAsync(new HunterLookupRequest
                    {
                        FullName = request.FullName,
                        AiWebsite = aiSearch.Company?.Website,
                        EmailHint = bestCandidate?.Email,
                        CompanyUrl = request.CompanyUrl,
                        Company = request.Company
                    });

                    hunter = new
                    {
                        Ran = true,
                        TriggeredAtConfidence = bestCandidate?.Confidence ?? 0,
                        ConfidenceThreshold = threshold,
                        lookup.Found,

                        // The unlock flow will not serve an address below the
                        // threshold, so callers of this endpoint can see the same
                        // verdict without re-deriving it from the score.
                        MeetsThreshold = lookup.Found && lookup.Score >= threshold,
                        lookup.Email,
                        lookup.Score,
                        lookup.VerificationStatus,
                        lookup.Domain,
                        lookup.Position,
                        lookup.SourceCount,
                        lookup.RejectedBecause
                    };
                }

                return Ok(new
                {
                    Success = true,
                    ClientId = clientId,
                    Model = modelName,
                    Provider = IsDeepSeekModel(modelName) ? "DeepSeek" : "OpenAI",
                    Results = aiSearch.Results,

                    // The employer facts the instruction asks for. Reported here
                    // so the prompt's company block is observable; nothing in
                    // the unlock flow reads them yet beyond the website, which
                    // is what Hunter is asked about.
                    Company = aiSearch.Company == null ? null : new
                    {
                        aiSearch.Company.Website,
                        aiSearch.Company.Industry,
                        aiSearch.Company.Size
                    },
                    Hunter = hunter,
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
            var company = searchResult.IsSuccess
                ? ParseFindEmailCompany(searchResult.Content ?? "")
                : null;

            return new AiEmailSearchOutcome(
                searchResult,
                modelName,
                finalPrompt,
                results,
                company);
        }

        /// <summary>
        /// Mode 3, and mode 4 behind it.
        ///
        /// Runs the AI search first. When the model's best candidate falls below
        /// the Hunter confidence threshold - or the model produced nothing at
        /// all - Hunter.io is asked as well and whichever answer scores higher
        /// is the one returned. A confident model answer never spends a Hunter
        /// request.
        ///
        /// Fills in <paramref name="diagnostics"/> as it goes so an admin can
        /// see the prompt, the model's raw reply, what Hunter said and which
        /// answer won.
        /// </summary>
        private async Task<UnlockEmailResult> CompleteAiFallbackUnlockAsync(
            ProspeoUnlockRequestDto request,
            UnlockDiagnostics diagnostics,
            string? rejectedEmail = null)
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
                Company = aiSearch.Company == null ? null : new UnlockAiCompany
                {
                    Website = aiSearch.Company.Website,
                    Industry = aiSearch.Company.Industry,
                    Size = aiSearch.Company.Size
                },
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

            void Stage(string name, string outcome, string detail, int elapsedMs) =>
                diagnostics.Stages.Add(new UnlockStageDiagnostics
                {
                    Name = name,
                    Outcome = outcome,
                    Detail = detail,
                    ElapsedMs = elapsedMs
                });

            string? aiEmail = null;
            var aiConfidence = 0;

            if (!aiSearch.SearchResult.IsSuccess)
            {
                // The model failing is no longer the end of the road: Hunter is
                // still worth asking, so this records the failure and carries on.
                ai.ChoiceReason = "The model call itself failed.";
                Stage("ai", "error",
                    "The " + ai.Provider + " call failed (" + ai.Model + ").",
                    (int)aiTimer.ElapsedMilliseconds);
            }
            else
            {
                // On a forced refresh the cached address is the one the caller is
                // telling us is wrong, so it sorts last - it is still kept,
                // because a wrong-looking address beats returning nothing at all.
                // After that a direct address beats a guessed pattern, then the
                // model's own confidence decides, and ties keep the order the
                // model returned.
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
                    .OrderBy(item => rejectedEmail != null &&
                        string.Equals(item.Email, rejectedEmail, StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(item =>
                        string.Equals(item.Type, "direct", StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(item => item.Confidence)
                    .ThenBy(item => item.Index)
                    .ToList();

                var chosen = ranked.FirstOrDefault();

                if (chosen == null || string.IsNullOrWhiteSpace(chosen.Email))
                {
                    ai.ChoiceReason = aiSearch.Results.Count == 0
                        ? "The model returned no candidates."
                        : "The model returned " + aiSearch.Results.Count +
                            " candidate(s), none of them a valid address.";
                    Stage("ai", "miss", ai.ChoiceReason, (int)aiTimer.ElapsedMilliseconds);
                }
                else
                {
                    aiEmail = chosen.Email;
                    aiConfidence = chosen.Confidence;

                    ai.ChosenEmail = aiEmail;
                    ai.ChoiceReason = "Picked from " + ranked.Count +
                        " valid candidate(s): type '" + chosen.Type +
                        "', confidence " + aiConfidence + ".";
                    Stage("ai", "hit", ai.ChoiceReason, (int)aiTimer.ElapsedMilliseconds);
                }
            }

            // ------------------------------------------------------ mode 4: Hunter
            var threshold = _hunterService.ConfidenceThreshold;
            string? hunterEmail = null;
            var hunterScore = 0;
            UnlockHunterDiagnostics? hunterDiagnostics = null;

            if (aiEmail != null && aiConfidence >= threshold)
            {
                Stage("hunter", "skipped",
                    "The model was " + aiConfidence + "% confident, at or above the " +
                    threshold + "% threshold, so Hunter was not called.",
                    0);
            }
            else
            {
                var triggerReason = aiEmail == null
                    ? "The AI stage produced no usable address."
                    : "The model was only " + aiConfidence + "% confident, below the " +
                      threshold + "% threshold.";

                var lookup = await _hunterService.FindEmailAsync(new HunterLookupRequest
                {
                    FullName = request.Name,

                    // The AI search is what actually researched this person, so
                    // its website is the domain to trust. What the extension
                    // scraped comes last: on a profile with no website on the
                    // page it is the company's LinkedIn URL, and Hunter cannot
                    // find anybody at linkedin.com.
                    AiWebsite = aiSearch.Company?.Website,

                    // A low-confidence guess is still usually right about the
                    // employer, so its domain stands in when the model reported
                    // no website.
                    EmailHint = aiEmail,
                    Domain = request.Domain,
                    CompanyUrl = request.CompanyUrl,
                    Company = request.CompanyName
                });

                hunterDiagnostics = new UnlockHunterDiagnostics
                {
                    ApiKeyConfigured = lookup.ApiKeyConfigured,
                    Endpoint = lookup.Endpoint,
                    RequestUrl = lookup.RequestUrl,
                    HttpStatus = lookup.HttpStatus,
                    RawResponse = lookup.RawResponse,
                    TriggeredAtConfidence = aiConfidence,
                    ConfidenceThreshold = threshold,
                    TriggerReason = triggerReason,
                    Email = lookup.Email,
                    Score = lookup.Score,
                    VerificationStatus = lookup.VerificationStatus,
                    Domain = lookup.Domain,
                    DomainSource = lookup.DomainSource,
                    Position = lookup.Position,
                    SourceCount = lookup.SourceCount,
                    RejectedBecause = lookup.RejectedBecause
                };
                diagnostics.Hunter = hunterDiagnostics;

                if (lookup.Found)
                {
                    hunterEmail = lookup.Email;
                    hunterScore = lookup.Score;
                }

                // "skipped" is for the stage never reaching Hunter at all - no key,
                // or nothing to search with. Once a request went out, an answer
                // without an address is a miss and a bad response is an error.
                // An address Hunter scores below the threshold is a miss too: it
                // is kept for the trace but cannot be served on its own.
                var outcome =
                    lookup.Found && lookup.Score >= threshold ? "hit"
                    : lookup.Found ? "miss"
                    : !lookup.ApiKeyConfigured ? "skipped"
                    : string.IsNullOrEmpty(lookup.RequestUrl) ? "skipped"
                    : lookup.HttpStatus is >= 200 and < 300 ? "miss"
                    : "error";

                Stage("hunter", outcome,
                    triggerReason + " " +
                    (!lookup.Found
                        ? lookup.RejectedBecause ?? "Hunter returned nothing usable."
                        : lookup.Score >= threshold
                            ? "Hunter returned " + hunterEmail + " with a score of " +
                              hunterScore + "."
                            : "Hunter returned " + hunterEmail + " but scored it " +
                              hunterScore + "%, below the " + threshold +
                              "% threshold, so it cannot be served on its own."),
                    lookup.ElapsedMs);
            }

            // Hunter's score and the model's confidence are both 0-100 statements
            // about the same address, so the higher one wins. A tie keeps the AI
            // answer, which already survived the ranking above.
            var preferHunter = hunterEmail != null &&
                (aiEmail == null || hunterScore > aiConfidence);

            // A forced refresh means the caller has called one address wrong.
            // Whichever stage repeats it loses to a stage offering something else.
            if (rejectedEmail != null && aiEmail != null && hunterEmail != null)
            {
                var aiRepeats = SameAddress(aiEmail, rejectedEmail);
                var hunterRepeats = SameAddress(hunterEmail, rejectedEmail);

                if (aiRepeats != hunterRepeats)
                    preferHunter = aiRepeats;
            }

            var email = preferHunter ? hunterEmail : aiEmail;

            if (hunterDiagnostics != null)
            {
                hunterDiagnostics.Preferred = preferHunter;
                hunterDiagnostics.ComparisonReason =
                    hunterEmail == null && aiEmail == null
                        ? "Neither stage produced an address."
                    : hunterEmail == null
                        ? "Hunter had nothing to compare, so the model's answer stands."
                    : aiEmail == null
                        ? "The model had no candidate, so Hunter's answer is the only one."
                    : preferHunter
                        ? "Hunter scored " + hunterScore + " against the model's " +
                          aiConfidence + ", so Hunter's address was used."
                        : "The model's " + aiConfidence + " was not beaten by Hunter's " +
                          hunterScore + ", so the model's address was used.";
            }

            // Neither stage vouching for its answer at the threshold is treated
            // as no answer. An address nobody is confident in would still be
            // billed, cached for 30 days and emailed, so it is worse than
            // reporting nothing found - the same bar Hunter was called to clear.
            var chosenConfidence = preferHunter ? hunterScore : aiConfidence;
            var belowThreshold = email != null && chosenConfidence < threshold;

            if (belowThreshold)
            {
                Stage("confidence", "miss",
                    "The best address found was " + email + " at " + chosenConfidence +
                    "%, below the " + threshold + "% threshold, so it was discarded and " +
                    "no credit was deducted.",
                    0);

                if (hunterDiagnostics != null)
                {
                    hunterDiagnostics.Preferred = false;
                    hunterDiagnostics.ComparisonReason +=
                        " Neither answer reached " + threshold +
                        "%, so no address was served.";
                }

                email = null;
            }

            // Nothing to return means every stage that ran came back empty or
            // came back under the threshold. Hunter always runs when the AI
            // stage does not clear it.
            if (string.IsNullOrWhiteSpace(email))
            {
                await ForgetRejectedCacheEntryAsync(request, rejectedEmail, diagnostics);

                diagnostics.Mode = "none";
                diagnostics.ModeReason = belowThreshold
                    ? "Neither the AI search nor Hunter reached " + threshold +
                      "% confidence, so no address was served."
                    : "Prospeo, the AI fallback and Hunter all came back empty.";

                return UnlockEmailResult.Failed(
                    request.ContactID,
                    belowThreshold
                        ? "No email found. Nothing reached the " + threshold +
                          "% confidence required, so no credit was deducted."
                        : "No email address was found by Prospeo, the AI fallback or Hunter. No credit was deducted.");
            }

            var repeatedRejected = SameAddress(email, rejectedEmail);

            if (repeatedRejected && !preferHunter)
            {
                ai.ChoiceReason += " This is the same address the cache held; the fresh lookup found nothing else.";
            }

            // Hunter winning makes this a mode 4 unlock, and the caller cannot
            // know that - it only knows it handed control to the AI fallback.
            if (preferHunter)
            {
                diagnostics.Mode = "hunter";
                diagnostics.ModeReason =
                    "The model was not confident enough, so Hunter was asked and its " +
                    "address scored higher.";
            }

            var completed = await _extensionRepository.CompleteProspeoUnlockAsync(
                request.ContactID,
                request.ClientID,
                request.LinkedInUrl,
                email);

            if (!completed)
            {
                return UnlockEmailResult.Failed(
                    request.ContactID,
                    "Email was found, but unlock could not complete because credit was unavailable.");
            }

            var foundBy = preferHunter ? "Hunter" : "AI fallback";

            return UnlockEmailResult.Succeeded(
                request.ContactID,
                email,
                repeatedRejected
                    ? "The fresh lookup returned the same address that was cached, and one credit was deducted."
                    : "Email found by " + foundBy + ", unlock history saved and one credit deducted.",
                preferHunter ? "hunter" : "ai");
        }

        /// <summary>Two addresses being the same one, case aside.</summary>
        private static bool SameAddress(string? left, string? right) =>
            !string.IsNullOrWhiteSpace(left) &&
            !string.IsNullOrWhiteSpace(right) &&
            string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
        /// <summary>
        /// A forced refresh that found nothing leaves the cache holding the very
        /// address the caller called wrong, and the next plain unlock would serve
        /// it straight back. Blank it instead - the unlock row stays for the
        /// history, only the address goes.
        /// </summary>
        private async Task ForgetRejectedCacheEntryAsync(
            ProspeoUnlockRequestDto request,
            string? rejectedEmail,
            UnlockDiagnostics diagnostics)
        {
            if (string.IsNullOrWhiteSpace(rejectedEmail))
                return;

            var cleared = await _extensionRepository.ClearProspeoUnlockedEmailAsync(
                request.LinkedInUrl,
                rejectedEmail);

            diagnostics.Stages.Add(new UnlockStageDiagnostics
            {
                Name = "cache",
                Outcome = cleared ? "cleared" : "skipped",
                Detail = cleared
                    ? "The fresh lookup found nothing, so the rejected address '" +
                      rejectedEmail + "' was dropped from the cache."
                    : "The rejected address was no longer in the cache.",
                ElapsedMs = 0
            });
        }

        private sealed record AiEmailSearchOutcome(
            PitchResult SearchResult,
            string ModelName,
            string FinalPrompt,
            JArray Results,
            AiCompanyDetails? Company);

        /// <summary>
        /// The employer facts the email instruction asks for alongside the
        /// addresses. Each is null when the model could not source it.
        /// </summary>
        private sealed record AiCompanyDetails(
            string? Website,
            string? Industry,
            string? Size);

        private static bool IsDeepSeekModel(string? modelName)
            => modelName?.StartsWith("deepseek-", StringComparison.OrdinalIgnoreCase) == true;

        /// <summary>
        /// The instruction asks for bare JSON, but models still wrap it in a
        /// ```json fence or add a sentence around it, so pull out the object and
        /// return its "results" array. Returns an empty array when the answer
        /// cannot be parsed — the raw text is always returned alongside.
        /// </summary>
        private static JArray ParseFindEmailResults(string content)
            => ExtractJsonObject(content)?["results"] as JArray ?? new JArray();

        /// <summary>
        /// The "company" block the same instruction asks for: the employer's
        /// website, industry and headcount band. Every field is null when the
        /// model could not source it, and the whole thing is null when the reply
        /// carried no company block at all.
        /// </summary>
        private static AiCompanyDetails? ParseFindEmailCompany(string content)
        {
            if (ExtractJsonObject(content)?["company"] is not JObject company)
                return null;

            return new AiCompanyDetails(
                Value(company["website"]),
                Value(company["industry"]),
                Value(company["size"]));

            // A model that has nothing to report writes JSON null, but "null"
            // and "Not provided" as strings both turn up too.
            static string? Value(JToken? token)
            {
                var text = token?.Type == JTokenType.Null
                    ? null
                    : token?.Value<string>()?.Trim();

                return string.IsNullOrWhiteSpace(text) ||
                       text.Equals("null", StringComparison.OrdinalIgnoreCase) ||
                       text.Equals(FindEmailPrompt.MissingValue, StringComparison.OrdinalIgnoreCase)
                    ? null
                    : text;
            }
        }

        /// <summary>
        /// Pulls the JSON object out of a model reply, whether it arrived bare,
        /// inside a ```json fence, or surrounded by a sentence of prose. Returns
        /// null when there is nothing parseable — the raw text is always
        /// returned to the caller alongside.
        /// </summary>
        private static JObject? ExtractJsonObject(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return null;

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
                    return null;

                text = text[start..(end + 1)];
            }

            try
            {
                return JsonConvert.DeserializeObject<JObject>(text);
            }
            catch (JsonException)
            {
                return null;
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
