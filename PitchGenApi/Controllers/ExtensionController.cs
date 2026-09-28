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
        private readonly QwenPitchService _qwenService;
        private readonly IAiModelSettingsService _aiModelSettings;
        private readonly IPromptSettingsService _promptSettings;
        private readonly IHunterEmailService _hunterService;
        private readonly IProspeoEmailService _prospeoService;
        private readonly IEmailUnlockService _emailUnlock;

        public ExtensionController(
            IExtensionRepository extensionRepository,
            IExtensionProfileService extensionProfileService,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            ContactRepository contactRepository,
            IPitchService pitchService,
            DeepSeekPitchService deepSeekService,
            QwenPitchService qwenService,
            IAiModelSettingsService aiModelSettings,
            IPromptSettingsService promptSettings,
            IHunterEmailService hunterService,
            IProspeoEmailService prospeoService,
            IEmailUnlockService emailUnlock)
        {
            _extensionRepository = extensionRepository;
            _extensionProfileService = extensionProfileService;
            _httpClientFactory = httpClientFactory;
            _configuration = configuration;
            _contactRepository = contactRepository;
            _pitchService = pitchService;
            _deepSeekService = deepSeekService;
            _qwenService = qwenService;
            _aiModelSettings = aiModelSettings;
            _promptSettings = promptSettings;
            _hunterService = hunterService;
            _prospeoService = prospeoService;
            _emailUnlock = emailUnlock;
        }

        [HttpPost("EX_prospeo-unlock")]
        public async Task<IActionResult> UnlockWithProspeo(
            [FromBody] ProspeoUnlockRequestDto request,
            CancellationToken cancellationToken)
        {
            if (request == null || request.ClientID <= 0 ||
                string.IsNullOrWhiteSpace(request.LinkedInUrl))
            {
                return BadRequest(UnlockEmailResult.Failed(
                    request?.ContactID,
                    "ClientID and LinkedInUrl are required."));
            }

            // The only thing the HTTP layer still decides. Everything after it
            // is the same four stages the validation run goes through.
            var isAdmin = await CallerIsAdminAsync(request.ClientID);

            return Ok(await _emailUnlock.UnlockAsync(request, isAdmin, cancellationToken));
        }


        /// <summary>
        /// Whether the caller may see an unlock trace. The token is shared with
        /// the web app and carries no admin claim, so this reads the flag from
        /// the database - and only after the Bearer token proves the caller is
        /// the client whose id is in the body, since this endpoint otherwise
        /// takes that id on trust.
        /// </summary>

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

                var aiSearch = await _emailUnlock.FindEmailWithAiCoreAsync(request, clientId);
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
                    Provider = EmailUnlockService.ProviderLabel(modelName),
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
