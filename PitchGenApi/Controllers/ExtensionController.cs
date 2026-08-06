using Microsoft.AspNetCore.Mvc;
using PitchGenApi.Interfaces;
using PitchGenApi.Model.DTOs;

namespace PitchGenApi.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class ExtensionController : ControllerBase
    {
        private readonly IExtensionRepository _extensionRepository;

        public ExtensionController(IExtensionRepository extensionRepository)
        {
            _extensionRepository = extensionRepository;
        }

        [HttpPost]
        public async Task<IActionResult> GetUnlockedEmail(
            GetUnlockedEmailRequest request,
            CancellationToken cancellationToken)
        {
            return Ok(await UnlockAsync(request, cancellationToken));
        }

        [HttpPost("multiple")]
        [HttpPost("GetMulitpleUnlockResults")]
        public async Task<IActionResult> GetMultipleUnlockResults(
            List<GetUnlockedEmailRequest> requests,
            CancellationToken cancellationToken)
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

        private async Task<UnlockEmailResult> UnlockAsync(
            GetUnlockedEmailRequest request,
            CancellationToken cancellationToken)
        {
            if (request == null ||
                string.IsNullOrWhiteSpace(request.ContactID) ||
                request.ClientID <= 0 ||
                string.IsNullOrWhiteSpace(request.Name) ||
                string.IsNullOrWhiteSpace(request.Domain))
            {
                return UnlockEmailResult.Failed(
                    request?.ContactID,
                    "ContactID, ClientID, Name and Domain are required.");
            }

            var status = new List<string>
            {
                $"{DateTime.UtcNow:O} Checking whether the contact was unlocked within 30 days."
            };

            var email = await _extensionRepository.GetUnlockedEmailAsync(
                request.ContactID,
                request.ClientID,
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
            var search = await FindValidEmailAsync(request, savedPatterns, status, cancellationToken);

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
                    cancellationToken);

                if (search.VerificationUnavailable)
                    return UnlockEmailResult.Failed(request.ContactID, string.Join("\n", status));

                email = search.Email;
            }

            if (string.IsNullOrWhiteSpace(email))
                return UnlockEmailResult.Failed(request.ContactID, string.Join("\n", status));

            return await CompleteUnlockAsync(request, email, status);
        }

        private async Task<EmailSearchResult> FindValidEmailAsync(
            GetUnlockedEmailRequest request,
            IEnumerable<string> patterns,
            List<string> status,
            CancellationToken cancellationToken)
        {
            foreach (var pattern in patterns.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var generatedEmail = _extensionRepository.GenerateEmail(
                    request.Name,
                    request.Domain,
                    pattern);

                Console.WriteLine($"Genrated Email = {generatedEmail}");

                if (string.IsNullOrWhiteSpace(generatedEmail))
                    continue;

                var validation = await _extensionRepository.Stage2Async(
                    generatedEmail,
                    cancellationToken);
                Console.WriteLine($"[EmailUnlock] GeneratedEmail={generatedEmail}");
                Console.WriteLine($"[EmailUnlock] VerificationState={validation.State}");
                Console.WriteLine($"[EmailUnlock] VerificationStatus={validation.Status}");
                status.Add($"{generatedEmail}: {validation.Status.Trim()}");

                Console.WriteLine($"Email Status = {validation}");


                if (validation.State == EmailVerificationState.Valid)
                {
                    var firstName = request.Name
                        .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .FirstOrDefault() ?? request.Name;
                    var stage3 = await _extensionRepository.Stage3Async(
                        generatedEmail,
                        firstName,
                        request.ContactID,
                        request.ClientID,
                        cancellationToken);
                    status.Add($"{generatedEmail}: {stage3.Status.Trim()}");
                    Console.WriteLine($"[EmailUnlock] Stage3State={stage3.State}");
                    Console.WriteLine($"[EmailUnlock] Stage3Status={stage3.Status}");

                    if (stage3.State == EmailVerificationState.Valid)
                        return new EmailSearchResult(generatedEmail, false);

                    if (stage3.State == EmailVerificationState.VerificationUnavailable)
                        return new EmailSearchResult(null, true);

                    // A hard bounce means this candidate is invalid; try the next pattern.
                    continue;
                }

                if (validation.State == EmailVerificationState.VerificationUnavailable)
                    return new EmailSearchResult(null, true);
            }

            return new EmailSearchResult(null, false);
        }

        private async Task<UnlockEmailResult> CompleteUnlockAsync(
            GetUnlockedEmailRequest request,
            string email,
            List<string> status)
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

            status.Add("Email found, contact updated, unlock history saved and one credit deducted.");
            return UnlockEmailResult.Succeeded(request.ContactID, email, string.Join("\n", status));
        }

        private sealed record EmailSearchResult(string? Email, bool VerificationUnavailable);
    }
}
