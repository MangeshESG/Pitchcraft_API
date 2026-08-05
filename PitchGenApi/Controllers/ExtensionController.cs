using Microsoft.AspNetCore.Http;
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
        public async Task<IActionResult> GetUnlockedEmail(GetUnlockedEmailRequest request)
        {
            var email = _extensionRepository.GetUnlockedEmail(request.LinkedInUrl);
            string stage2Status;

            if (string.IsNullOrEmpty(email))
            {
                var emailPatterns = await _extensionRepository.GetEmailPatternsAsync(request.Domain);
                stage2Status = string.Empty;

                foreach (var emailPattern in emailPatterns)
                {
                    var generatedEmail = _extensionRepository.GenerateEmail(
                        request.Name,
                        request.Domain,
                        emailPattern);

                    if (string.IsNullOrEmpty(generatedEmail))
                        continue;

                    var (isGeneratedEmailValid, generatedEmailStatus) =
                        await _extensionRepository.Stage2Async(generatedEmail);

                    stage2Status = generatedEmailStatus;

                    if (isGeneratedEmailValid)
                    {
                        email = generatedEmail;
                        break;
                    }
                }

                if (string.IsNullOrEmpty(email))
                {
                    return Ok(new
                    {
                        success = false,
                        email = string.Empty,
                        status = stage2Status
                    });
                }
            }
            else
            {
                var (isValid, unlockedEmailStatus) = await _extensionRepository.Stage2Async(email);
                stage2Status = unlockedEmailStatus;

                if (!isValid)
                {
                    return Ok(new
                    {
                        success = false,
                        email = string.Empty,
                        status = stage2Status
                    });
                }
            }

            await _extensionRepository.UpdateContactEmailAsync(request.LinkedInUrl, email, request.ClientID);

            return Ok(new
            {
                success = true,
                email = email,
                status = stage2Status
            });
        }
    }
}
