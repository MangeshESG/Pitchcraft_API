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

            if (!int.TryParse(User.FindFirst("UserId")?.Value, out var authenticatedClientId) ||
                authenticatedClientId != request.ClientID)
            {
                return Forbid();
            }

            if (!await _contactRepository.HasAvailableCreditAsync(request.ClientID))
            {
                return Ok(UnlockEmailResult.Failed(
                    request.ContactID,
                    "No unlock credit is available. Please buy credits to unlock this email."));
            }

            var cachedEmail = await _extensionRepository.GetProspeoUnlockedEmailAsync(
                request.LinkedInUrl);
            if (!string.IsNullOrWhiteSpace(cachedEmail))
            {
                var cachedCompleted = await _extensionRepository.CompleteProspeoUnlockAsync(
                    request.ContactID,
                    request.ClientID,
                    request.LinkedInUrl,
                    cachedEmail);

                return Ok(cachedCompleted
                    ? UnlockEmailResult.Succeeded(
                        request.ContactID,
                        cachedEmail,
                        "Email reused from the 30-day unlock cache and one credit deducted.")
                    : UnlockEmailResult.Failed(
                        request.ContactID,
                        "No unlock credit is available. Please buy credits to unlock this email."));
            }

            var apiKey = _configuration["Prospeo:ApiKey"];
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable,
                    UnlockEmailResult.Failed(
                        request.ContactID,
                        "Email enrichment is not configured."));
            }

            try
            {
                using var httpRequest = new HttpRequestMessage(
                    HttpMethod.Post,
                    "https://api.prospeo.io/enrich-person");
                httpRequest.Headers.Add("X-KEY", apiKey);
                httpRequest.Content = JsonContent.Create(new
                {
                    only_verified_email = true,
                    enrich_mobile = false,
                    data = new { linkedin_url = request.LinkedInUrl.Trim() }
                });

                var client = _httpClientFactory.CreateClient();
                using var response = await client.SendAsync(httpRequest, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    return StatusCode(StatusCodes.Status502BadGateway,
                        UnlockEmailResult.Failed(
                            request.ContactID,
                            "The email enrichment provider could not complete the request."));
                }

                var result = await response.Content.ReadFromJsonAsync<ProspeoEnrichResponseDto>(
                    cancellationToken: cancellationToken);
                var emailResult = result?.Person?.Email;
                var email = emailResult?.Email?.Trim();

                if (result?.Error == true || emailResult == null ||
                    !emailResult.Revealed ||
                    !string.Equals(emailResult.Status, "VERIFIED", StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(email))
                {
                    return Ok(UnlockEmailResult.Failed(
                        request.ContactID,
                        "No verified email address was found for this LinkedIn profile."));
                }

                var completed = await _extensionRepository.CompleteProspeoUnlockAsync(
                    request.ContactID,
                    request.ClientID,
                    request.LinkedInUrl,
                    email);

                if (!completed)
                {
                    return Ok(UnlockEmailResult.Failed(
                        request.ContactID,
                        "No unlock credit is available. Please buy credits to unlock this email."));
                }

                return Ok(UnlockEmailResult.Succeeded(
                    request.ContactID,
                    email,
                    "Verified email unlocked and one credit deducted."));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return StatusCode(StatusCodes.Status504GatewayTimeout,
                    UnlockEmailResult.Failed(request.ContactID, "Email enrichment timed out."));
            }
            catch (HttpRequestException)
            {
                return StatusCode(StatusCodes.Status502BadGateway,
                    UnlockEmailResult.Failed(
                        request.ContactID,
                        "The email enrichment provider is unavailable."));
            }
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
        /// Summarises the scraped LinkedIn profile with the LLM and stores it in
        /// the contact's LinkedIn information field.
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

                // The instruction is fixed in code — only the contact details
                // come from the caller.
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

                // Finding an email means reaching the live web, so this goes down
                // the same web-search path as the research step — DeepSeek or
                // OpenAI depending on the configured model. Both deduct the one
                // credit for the client they are handed.
                var searchResult = IsDeepSeekModel(modelName)
                    ? await _deepSeekService.GenerateWebSearchAsync(
                        enquiryRequest,
                        clientId)
                    : await _pitchService.GenerateWebSearchAsync(
                        enquiryRequest,
                        clientId);

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
                    Results = ParseFindEmailResults(raw),
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
