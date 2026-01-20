using Microsoft.AspNetCore.Mvc;
using PitchGenApi.Interfaces;
using static System.Net.WebRequestMethods;

namespace PitchGenApi.Controllers
{
    [ApiController]
    [Route("api/domain-verification")]
    public class DomainVerificationController : ControllerBase
    {
        private readonly IDomainVerificationRepository _repo;

        public DomainVerificationController(IDomainVerificationRepository repo)
        {
            _repo = repo;
        }
        
        // ===============================
        // Verify Domain via DNS
        // ===============================
        [HttpPost("verify")]
        public async Task<IActionResult> VerifyDomain(string domain, int clientId)
        {
            var result = await _repo.VerifyDomain(domain, clientId);

            if (!result.Success)
                return BadRequest(result.Message);

            return Ok(result.Message);
        } 
       
        [HttpPost("verifySmtpOtp")]
        public async Task<IActionResult> VerifySmtpOtp(string email,string otp,string clientId)
        {
            var result = await _repo.VerifySmtpOtp(email, otp, clientId);
            if (result.Success)
            {
                var Domain = await _repo.VerifySpfDkimDmarc(email, clientId);

            }

            if (!result.Success)
                return BadRequest(result);

            return Ok(result);
        }

        [HttpGet("get-verified-domain")]
        public async Task<IActionResult> GetVerifiedDomain(int clientId)
        {

            var result = await _repo.GetVerifiedDomain(clientId);

            if (!result.Success)
                return BadRequest(result.Message);

            return Ok(result.Data);
        }
        [HttpGet("get-verified")]
        public async Task<IActionResult> VerifySpfDkimDmarc(string email,string clientId)
        {

            var result = await _repo.VerifySpfDkimDmarc(email,clientId);

            if (!result.Success)
                return BadRequest(result.Message);

            return Ok(result.Data);
        }

        [HttpGet("verify-email-signature")]
        public async Task<IActionResult> VerifyEmailSignature(string domain, string? DMARC, string? expectedDMARcValue, string? DKIM,string? expectedDkimValue, int clientId)
        {
            bool? dkimResult = null;
            bool? dmarcResult = null;

            var errors = new List<string>();

            // DKIM
            if (!string.IsNullOrWhiteSpace(DKIM))
            {
                dkimResult = await _repo.CustomDKIM(domain, DKIM, expectedDkimValue, clientId);

                if (dkimResult == false)
                    errors.Add("DKIM record not found or invalid");
            }

            // DMARC
            if (!string.IsNullOrWhiteSpace(DMARC))
            {
                dmarcResult = await _repo.CustomDMARC(domain, DMARC, expectedDMARcValue, clientId);

                if (dmarcResult == false)
                    errors.Add("DMARC record not found or invalid");
            }

            // ❌ Both failed or any failed → BadRequest with details
            if (errors.Any())
            {
                return BadRequest(new
                {
                    DKIM = dkimResult,
                    DMARC = dmarcResult,
                    Errors = errors
                });
            }

            // ✅ Both succeeded (or skipped)
            return Ok(new
            {
                DKIM = dkimResult,
                DMARC = dmarcResult
            });
        }

        [HttpPost("delete-domain")]
        public async Task<IActionResult> DeleteDomain([FromQuery] int domainId, [FromQuery] string clientId)
        {
            if (domainId <= 0)
                return BadRequest("Invalid domainId or clientId");

            var result = await _repo.DeleteDomainAsync(domainId, clientId);

            if (!result.Success)
                return BadRequest(result.Message);

            return Ok(result.Message);
        }

    }
}
