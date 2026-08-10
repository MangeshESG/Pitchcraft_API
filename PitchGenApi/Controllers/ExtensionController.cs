using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PitchGenApi.Interfaces;
using PitchGenApi.Model.DTOs;
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

        public ExtensionController(
            IExtensionRepository extensionRepository,
            IExtensionProfileService extensionProfileService,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            ContactRepository contactRepository)
        {
            _extensionRepository = extensionRepository;
            _extensionProfileService = extensionProfileService;
            _httpClientFactory = httpClientFactory;
            _configuration = configuration;
            _contactRepository = contactRepository;
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
