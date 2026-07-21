using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PitchGenApi.Interfaces;
using PitchGenApi.Model.DTOs;

namespace PitchGenApi.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class ForwardController : ControllerBase
    {
        private readonly IForwardRepository _forword;

        public ForwardController(IForwardRepository forword)
        {
            _forword = forword;
        }
        // Controller API

        [HttpPost("forward-email")]
        public async Task<IActionResult> ForwardEmail([FromBody] ForwardEmailDto dto)
        {
            if (dto == null)
            {
                return BadRequest(new
                {
                    success = false,
                    message = "Invalid request"
                });
            }

            EmailSendResult result;

            switch (dto.Provider?.ToUpper())
            {
                case "IMAP" or "SMTP":

                    result = await _forword.ForwardEmailUsingSmtp(
                        dto.TrackingId,
                        dto.ClientId,
                        dto.ForwardToEmail,
                        dto.ForwardMessage,
                        dto.OutboxId,
                        dto.CcEmail,
                        dto.BccEmail);

                    break;

                case "GMAIL":

                    result = await _forword.ForwardEmailUsingGmailApi(
                        dto.TrackingId,
                        dto.ClientId,
                        dto.ForwardToEmail,
                        dto.ForwardMessage,
                        dto.OutboxId,
                        dto.CcEmail,
                        dto.BccEmail);

                    break;

                case "OUTLOOK":

                    result = await _forword.ForwardEmailUsingOutlookApi(
                        dto.TrackingId,
                        dto.ClientId,
                        dto.ForwardToEmail,
                        dto.ForwardMessage,
                        dto.OutboxId,
                        dto.CcEmail,
                        dto.BccEmail);

                    break;

                default:

                    return BadRequest(new
                    {
                        success = false,
                        message = "Invalid provider"
                    });
            }

            if (!result.Success)
            {
                return BadRequest(new
                {
                    success = false,
                    message = result.Message
                });
            }

            return Ok(new
            {
                success = true,
                message = result.Message
            });
        }
    }
}


