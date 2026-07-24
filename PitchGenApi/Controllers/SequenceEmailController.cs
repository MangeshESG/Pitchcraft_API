using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PitchGenApi.Database;
using PitchGenApi.Model;
using PitchGenApi.Model.DTOs;
using PitchGenApi.Services;
using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Net.Mail;
using System.Net;
using PitchGenApi.Models;
using PitchGenApi.Interfaces;
using System.Text.Json;
using Stripe;
using MailKit.Security;
using MimeKit;
using static SequenceCreateDto;

namespace PitchGenApi.Controllers
{
    [ApiController]
    [Route("api/email")]
    public class SequenceEmailController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly ContactRepository _contactRepository;
        private readonly EmailSendingHelper _emailHelper;
        private readonly IDomainVerificationRepository _repo;
        private readonly IReplyEmailRepository _replyRepo;
        private readonly IInboxRepository _inboxRepository;


        public SequenceEmailController(AppDbContext context, ContactRepository contactRepository, EmailSendingHelper emailHelper, IDomainVerificationRepository repository,IReplyEmailRepository replyRepo, IInboxRepository inboxRepository)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _contactRepository = contactRepository;
            _emailHelper = emailHelper;
            _repo = repository;
            _replyRepo = replyRepo;
            _inboxRepository = inboxRepository;
        }

        // Step 1: Create a new email sequence with multiple steps
        [HttpPost("create-sequence")]
        public async Task<IActionResult> CreateSequence([FromQuery] string ClientId, [FromBody] SequenceCreateDto dto)
        {
            if (dto == null)
                return BadRequest(new { message = "Request body is missing or invalid." });

            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            if (dto.Steps == null || dto.Steps.Count == 0)
                return BadRequest(new { message = "Sequence steps cannot be empty." });

            // Validate TimeZone
            TimeZoneInfo clientTimeZone;
            try
            {
                clientTimeZone = TimeZoneInfo.FindSystemTimeZoneById(dto.TimeZone);
            }
            catch (TimeZoneNotFoundException)
            {
                return BadRequest(new { message = $"Invalid TimeZone ID: {dto.TimeZone}" });
            }

            var outboxExists = await SequenceOutboxExistsAsync(dto.Provider, dto.SmtpID, ClientId);

            if (!outboxExists)
            {
                return BadRequest(new
                {
                    message = $"Invalid {dto.Provider} outbox ID: {dto.SmtpID}. No {dto.Provider} configuration found for this client."
                });
            }

            var now = DateTime.UtcNow;
            var newSteps = new List<SequenceStep>();

            try
            {
                foreach (var step in dto.Steps)
                {
                    // Your frontend sends UTC time, so use it directly
                    var utcDateTime = step.ScheduledDate.Date + step.ScheduledTime;

                    var entity = new SequenceStep
                    {
                        ClientId = Convert.ToInt32(ClientId),
                        Title = dto.Title?.Trim() ?? string.Empty,
                        CreatedAt = now,
                        ScheduledDate = utcDateTime.Date,
                        ScheduledTime = utcDateTime.TimeOfDay,
                        TimeZone = dto.TimeZone,
                        zohoviewName = dto.zohoviewName?.Trim() ?? string.Empty,
                        BccEmail = dto.BccEmail,
                        DataFileId = dto.SegmentId.HasValue ? null : dto.DataFileId, 
                        SegmentId = dto.DataFileId.HasValue ? null : dto.SegmentId,
                        CampaignId = dto.CampaignId,
                        TestIsSent = false,
                        SmtpID = dto.SmtpID,
                        Provider = dto.Provider,
                        IsSent = true,
                        IsFollowUp = dto.IsFollowUp
                    };

                    newSteps.Add(entity);
                }

                await _context.SequenceSteps.AddRangeAsync(newSteps);
                await _context.SaveChangesAsync();

                return Ok(new { message = $"{newSteps.Count} sequence step(s) saved successfully." });
            }
            catch (DbUpdateException dbEx)
            {
                return StatusCode(500, new { message = "Database error occurred.", detail = dbEx.InnerException?.Message ?? dbEx.Message });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "An unexpected error occurred.", detail = ex.Message });
            }
        }

        // Step 3: Save SMTP credentials for the logged-in client
        [HttpPost("save-smtp")]
        public async Task<IActionResult> SaveSmtp([FromQuery] string ClientId, [FromBody] SmtpCredentialDto dto)
        {
            if (dto == null)
                return BadRequest("Request body is null.");

            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            //var userId = User.FindFirst("UserId")?.Value;

            //if (string.IsNullOrEmpty(userId))
            //    return Unauthorized("Client not logged in");

            var smtp = new SmtpCredentials
            {
                ClientId = ClientId,
                Server = dto.OutgoingServer,
                Port = dto.OutgoingPort,
                Username = dto.Username,
                Password = dto.Password,
                FromEmail = dto.FromEmail,
            };

            _context.SmtpCredentials.Add(smtp);
            await _context.SaveChangesAsync();

            return Ok(new { message = "SMTP credentials saved successfully." });
        }

        [HttpPost("Update-smtp/{id:int}")]
        public async Task<IActionResult> UpdateSmtp(int id, [FromQuery] string ClientId, [FromBody] SmtpCredentialDto dto)
        {
            try
            {
                if (!int.TryParse(ClientId, out int userId))
                    return BadRequest("Invalid ClientId");

                var smtp = await _context.SmtpCredentials
                    .FirstOrDefaultAsync(s => s.Id == id && s.ClientId == ClientId);

                if (smtp == null)
                    return NotFound("SMTP credentials not found.");

                // Existing inbox
                var imap = await _context.Inboxcredentials
                    .FirstOrDefaultAsync(x => x.Outboxid == id && x.ClientId == userId);

                // =========================
                // SMTP TEST
                // =========================
                try
                {
                    using var smtpClient = new MailKit.Net.Smtp.SmtpClient();

                    var socketOption = _inboxRepository
                        .GetSecureOption(dto.OutgoingSecurityType);

                    await smtpClient.ConnectAsync(
                        dto.OutgoingServer,
                        dto.OutgoingPort,
                        socketOption);

                    await smtpClient.AuthenticateAsync(
                        dto.Username,
                        dto.Password);

                    var toMessage = new MimeMessage();

                    toMessage.From.Add(
                        new MailboxAddress(dto.SenderName, dto.FromEmail));

                    toMessage.To.Add(
                        MailboxAddress.Parse("support@pitchkraft.ai"));

                    toMessage.Subject = "SMTP Configuration Test";

                    toMessage.Body = new BodyBuilder
                    {
                        HtmlBody = $@"
                <html>
                <body style='font-family: Arial, sans-serif; color:#333; line-height:1.6;'>

                    <p>Hello,</p>

                    <p>
                        This is a test email sent from 
                        <b>{dto.SenderName}</b>
                        ({dto.FromEmail})
                        to verify outgoing email functionality.
                    </p>

                    <p>
                        If you have received this message, 
                        the SMTP configuration is working correctly.
                    </p>

                    <br/>

                    <p>
                        Best regards,<br/>
                        <b>{dto.SenderName}</b><br/>
                        {dto.FromEmail}
                    </p>

                </body>
                </html>"
                    }.ToMessageBody();

                    await smtpClient.SendAsync(toMessage);

                    if (smtpClient.IsConnected)
                        await smtpClient.DisconnectAsync(true);
                }
                catch (Exception smtpEx)
                {
                    return BadRequest(new
                    {
                        message = "SMTP test failed. Please check outgoing details.",
                        detail = smtpEx.Message
                    });
                }

                // =========================
                // INBOX / IMAP VALIDATION
                // =========================
                if (dto.Inbox != null)
                {
                    var isValid = await _inboxRepository.ValidateAsync(dto.Inbox);

                    if (!isValid)
                    {
                        return BadRequest(new
                        {
                            message = "Invalid inbox credentials. Please check inbox details."
                        });
                    }

                    // =========================
                    // UPDATE EXISTING IMAP
                    // =========================
                    if (imap != null)
                    {
                        imap.EmailAddress = dto.Inbox.EmailAddress;
                        imap.Username = dto.Inbox.Username;
                        imap.Password = dto.Inbox.Password;
                        imap.encryption = dto.Inbox.encryption;
                        imap.Host = dto.Inbox.Host;
                        imap.Port = dto.Inbox.Port;
                        imap.Protocol = "IMAP";
                        imap.FullInboxSync = dto.Inbox.FullInboxSync;
                        imap.UpdatedAt = DateTime.UtcNow;

                        _context.Inboxcredentials.Update(imap);
                    }
                    else
                    {
                        // =========================
                        // CREATE NEW IMAP
                        // =========================
                        var newInbox = new Inboxcredentials
                        {
                            ClientId = userId,
                            EmailAddress = dto.Inbox.EmailAddress,
                            Username = dto.Inbox.Username,
                            Password = dto.Inbox.Password,
                            encryption = dto.Inbox.encryption,
                            Host = dto.Inbox.Host,
                            Port = dto.Inbox.Port,
                            Protocol = "IMAP",
                            FullInboxSync = dto.Inbox.FullInboxSync,
                            Outboxid = smtp.Id,
                            CreatedAt = DateTime.UtcNow,
                            UpdatedAt = DateTime.UtcNow
                        };

                        await _context.Inboxcredentials.AddAsync(newInbox);
                    }
                }

                // =========================
                // UPDATE SMTP
                // =========================
                smtp.Server = dto.OutgoingServer;
                smtp.Port = dto.OutgoingPort;
                smtp.Username = dto.Username;
                smtp.Password = dto.Password;
                smtp.FromEmail = dto.FromEmail;
                smtp.SenderName = dto.SenderName;
                smtp.SecurityType = dto.OutgoingSecurityType;

                _context.SmtpCredentials.Update(smtp);

                await _context.SaveChangesAsync();

                return Ok(new
                {
                    message = "SMTP and inbox credentials updated successfully."
                });
            }
            catch (DbUpdateException dbEx)
            {
                return StatusCode(500, new
                {
                    message = "Database error occurred.",
                    detail = dbEx.Message
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    message = "An unexpected error occurred.",
                    detail = ex.Message
                });
            }
        }

        [HttpPost("delete-smtp/{id:int}")]
        public async Task<IActionResult> DeleteSmtp(int id, [FromQuery] string ClientId)
        {
            if (string.IsNullOrWhiteSpace(ClientId))
                return BadRequest("ClientId is required.");

            if (!int.TryParse(ClientId, out int userId))
                return BadRequest("Invalid ClientId");


            try
            {
                // Step 1: SMTP record get karo without accessing any properties directly
                var smtp = await _context.SmtpCredentials
                    .Where(s => s.Id == id && s.ClientId == ClientId)
                    .FirstOrDefaultAsync();

                var imap = await _context.Inboxcredentials
                    .Where(s => s.Outboxid == id && s.ClientId == userId)
                    .FirstOrDefaultAsync();

                var emaildomain = await _context.DomainEmailVerification.FirstOrDefaultAsync(x => x.Email == smtp.FromEmail);

                if (smtp == null)
                    return NotFound("SMTP credentials not found for this client.");

                // Step 2: Related SequenceSteps ko safely fetch karo (null-safe)
                var sequenceSteps = await _context.SequenceSteps
                    .Where(s => s.SmtpID == id)
                    .ToListAsync();

                if (sequenceSteps?.Count > 0)
                {
                    _context.SequenceSteps.RemoveRange(sequenceSteps);
                }

                if (emaildomain != null)
                {
                    _context.DomainEmailVerification.Remove(emaildomain);
                }

                if (imap != null)
                {
                    _context.Inboxcredentials.Remove(imap);
                }

                // Step 3: Delete SMTP record (even if some columns are null)
                _context.SmtpCredentials.Remove(smtp);
                await _context.SaveChangesAsync();

                return Ok(new { message = "SMTP credentials deleted successfully." });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    message = "An unexpected error occurred.",
                    detail = ex.Message,
                    inner = ex.InnerException?.Message
                });
            }
        }


        [HttpGet("get-smtp")]
        public async Task<IActionResult> GetSmtp([FromQuery] string ClientId)
        {
            try
            {
                if (!int.TryParse(ClientId, out int clientId))
                    return BadRequest("Invalid ClientId");

                var smtpList = await _context.SmtpCredentials
                    .Where(s => s.ClientId == ClientId)
                    .Where(s => s.Server != null
                                && s.Username != null
                                && s.Password != null
                                && s.FromEmail != null)
                    .ToListAsync();

                if (smtpList == null || smtpList.Count == 0)
                    return NotFound("No SMTP credentials found for this client.");

                // Inbox credentials load karo
                var inboxList = await _context.Inboxcredentials
                    .Where(i => i.ClientId == clientId)
                    .ToListAsync();

                var result = smtpList.Select(smtp =>
                {
                    // SMTP Id ke basis pe inbox find karo
                    var inbox = inboxList
                        .FirstOrDefault(i => i.Outboxid == smtp.Id);

                    return new
                    {
                        smtp.Id,
                        smtp.ClientId,
                        smtp.Server,
                        smtp.Port,
                        smtp.Username,
                        smtp.Password,
                        smtp.UseSsl,
                        smtp.FromEmail,
                        smtp.SenderName,
                        smtp.SecurityType,

                        Inbox = inbox == null ? null : new
                        {
                            inbox.Id,
                            inbox.EmailAddress,
                            inbox.Host,
                            inbox.Port,
                            inbox.FullInboxSync,
                            inbox.Username,
                            inbox.Password,
                            inbox.Outboxid,
                            inbox.encryption,
                            inbox.UpdatedAt
                        }
                    };
                });

                return Ok(result);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    message = "An unexpected error occurred.",
                    detail = ex.Message
                });
            }
        }


        [HttpPost("send-singleEmail")]
        public async Task<IActionResult> SendSingleEmail([FromBody] SendEmailRequestDto dto)
        {
            try
            {
                ServicePointManager.ServerCertificateValidationCallback =
                    (sender, certificate, chain, sslPolicyErrors) => true;

                var nowUtc = DateTime.UtcNow;

                // ? Correct type use
                EmailSendResult success = new EmailSendResult
                {
                    Success = false,
                    Message = "Invalid Type"
                };

                // ? Switch case
                switch (dto.Type?.ToUpper())
                {
                    case "IMAP" or "SMTP":
                        success = await _emailHelper.SendEmailUsingSmtp(
                            dto.clientId,
                            dto.contactid,
                            dto.campaignid,
                            dto.isFollowUp,
                            dto.CcEmail,
                            dto.BccEmail,
                            dto.Outboxid
                        );
                        break;

                    case "OUTLOOK":
                        success = await _emailHelper.SendEmailUsingOutlookApi(
                            dto.clientId,
                            dto.contactid,
                            dto.campaignid,
                            dto.isFollowUp,
                            dto.CcEmail,
                            dto.BccEmail,
                            dto.Outboxid
                        );
                        break;

                    case "GMAIL":
                        success = await _emailHelper.SendEmailUsingGmailApi(
                            dto.clientId,
                            dto.contactid,
                            dto.campaignid,
                            dto.isFollowUp,
                            dto.CcEmail,
                            dto.BccEmail,
                            dto.Outboxid
                        );
                        break;

                    default:
                        return BadRequest(new
                        {
                            message = "Invalid email type. Use SMTP or GMAIL."
                        });
                }

                // ? Fail case
                if (!success.Success)
                {
                    return StatusCode(500, new
                    {
                        message = success.Message
                    });
                }

                if (dto.contactid > 0)
                {
                    var contact = await _context.contacts
                        .FirstOrDefaultAsync(c => c.id == dto.contactid);

                    if (contact != null)
                    {
                        contact.email_sent_at = nowUtc;
                        await _context.SaveChangesAsync();
                    }
                }

                // ? Final response
                return Ok(new
                {
                    message = success.Message,
                    emailSentAtUtc = nowUtc
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    message = "Unexpected error",
                    error = ex.Message
                });
            }
        }



        [HttpPost("reply_email")]
        [Consumes("multipart/form-data")]
        public async Task<IActionResult> ReplyEmail([FromForm] ReplyEmailRequest request)
        {
            if (request.TrackingId == Guid.Empty)
                return BadRequest("TrackingId is required");

            if (string.IsNullOrWhiteSpace(request.ReplyBody))
                return BadRequest("Reply body is required");

            if (string.IsNullOrWhiteSpace(request.Provider))
                return BadRequest("Provider is required");

            EmailSendResult result;

            switch (request.Provider.ToUpper())
            {
                case "IMAP" or "SMTP":
                    result = await _replyRepo.ReplyEmailUsingSmtp(
                        request.TrackingId,
                        request.ClientId,
                        request.ReplyBody,
                        request.Outboxid,
                        request.BCC,
                        request.CC,
                        request.Attachments
                    );
                    break;

                case "GMAIL":
                    result = await _replyRepo.ReplyEmailUsingGmailApi(
                        request.TrackingId,
                        request.ClientId,
                        request.ReplyBody,
                        request.Outboxid,
                        request.BCC,
                        request.CC,
                        request.Attachments);
                    break;

                case "OUTLOOK":
                    result = await _replyRepo.ReplyEmailUsingOutlookApi(
                        request.TrackingId,
                        request.ClientId,
                        request.ReplyBody,
                        request.Outboxid,
                        request.BCC,
                        request.CC,
                        request.Attachments
                    );
                    break;

                default:
                    return BadRequest("Invalid provider. Use SMTP, Gmail, or Outlook.");
            }

            if (!result.Success)
                return BadRequest(result);

            return Ok(result);
        }

        //[HttpPost("send-singleEmail")]
        //public async Task<IActionResult> SendSingleEmail([FromQuery] int clientId, [FromQuery] int dataFileId, [FromQuery] int? contactId = null, [FromQuery] int smtpId = 0, [FromQuery] string bccEmail = null)
        //{
        //    try
        //    {
        //        ServicePointManager.ServerCertificateValidationCallback = (sender, certificate, chain, sslPolicyErrors) => true;

        //        // Get contact with optional next
        //        var contactWithNext = await _contactRepository.GetContactWithNextAsync(dataFileId, contactId);
        //        if (contactWithNext == null || contactWithNext.CurrentContact == null || string.IsNullOrWhiteSpace(contactWithNext.CurrentContact.email))
        //            return BadRequest("No valid contact found for the given DataFileId and ContactId.");

        //        var contact = contactWithNext.CurrentContact;

        //        // Basic values
        //        string toEmail = contact.email;
        //        string subject = contact.email_subject ?? "No Subject";
        //        string rawBody = contact.email_body ?? "No Content";
        //        string body = string.IsNullOrWhiteSpace(rawBody) ? "No content provided." : rawBody;

        //        // Send email using SMTP
        //        var success = await _emailHelper.SendEmailUsingSmtp(
        //            clientId,
        //            dataFileId,
        //            toEmail,
        //            subject,
        //            body,
        //            bccEmail,
        //            smtpId,
        //            dataFileId.ToString(),
        //            contact.full_name,
        //            contact.country_or_address,
        //            contact.company_name,
        //            contact.website,
        //            contact.linkedin_url,
        //            contact.job_title
        //        );

        //        if (!success)
        //            return StatusCode(500, "Failed to send email. Please try again later.");

        //        return Ok(new
        //        {
        //            message = $"Email sent successfully to {toEmail}.",
        //            contactName = contact.full_name,
        //            currentContactId = contact.id,
        //            nextContactId = contactWithNext.NextContactId
        //        });
        //    }
        //    catch (Exception ex)
        //    {
        //        return StatusCode(500, $"Unexpected error: {ex.Message}");
        //    }
        //}


        [HttpPost("configTestMail")]
        public async Task<IActionResult> configTestMail([FromQuery] string ClientId, [FromBody] SmtpCredentialDto dto)
        {
            ServicePointManager.ServerCertificateValidationCallback = (sender, certificate, chain, sslPolicyErrors) => true;

            if (string.IsNullOrEmpty(ClientId))
                return BadRequest("ClientId required");

            if (string.IsNullOrWhiteSpace(dto.FromEmail) || !dto.FromEmail.Contains("@"))
                return BadRequest("Invalid email");

            var existingRecord = await _context.SmtpCredentials
                .FirstOrDefaultAsync(x => x.ClientId == ClientId && x.FromEmail == dto.FromEmail);

            if (dto.IsUpdate == false && existingRecord != null)
            {
                return BadRequest("Email already exists");
            }

            // ?? STEP 1: SMTP FAST FAIL (NO DB TOUCH)
            try
            {
                using var smtpClient = new MailKit.Net.Smtp.SmtpClient();

                var socketOption = _inboxRepository.GetSecureOption(dto.OutgoingSecurityType);

                await smtpClient.ConnectAsync(dto.OutgoingServer, dto.OutgoingPort, socketOption);
                await smtpClient.AuthenticateAsync(dto.Username, dto.Password);

                var toMessage = new MimeMessage();

                toMessage.From.Add(new MailboxAddress(dto.SenderName, dto.FromEmail));
                toMessage.To.Add(MailboxAddress.Parse("support@pitchkraft.ai"));
                toMessage.Subject = "SMTP Configuration Test";

                toMessage.Body = new BodyBuilder
                {
                    HtmlBody = $@"
                        <html>
                        <body style='font-family: Arial, sans-serif; color:#333; line-height:1.6;'>
                            <p>Hello,</p>

                            <p>
                                This is a test email sent from <b>{dto.SenderName}</b>
                                (<a href='mailto:{dto.FromEmail}'>{dto.FromEmail}</a>)
                                to verify outgoing email functionality.
                            </p>

                            <p>
                                If you have received this message, the email setup is working correctly.
                            </p>

                            <br/>

                            <p>
                                Best regards,<br/>
                                <b>{dto.SenderName}</b><br/>
                                {dto.FromEmail}
                            </p>
                        </body>
                        </html>"
                }.ToMessageBody();

                await smtpClient.SendAsync(toMessage);
                if (smtpClient.IsConnected)
                {
                    await smtpClient.DisconnectAsync(true);
                }
                
                if (dto.Inbox != null)
                {
                    var isValid = await _inboxRepository.ValidateAsync(dto.Inbox);

                    if (!isValid)
                        return BadRequest("Invalid inbox credentials or unable to connect to server.");
                }
            }
            catch (Exception ex)
            {
                return BadRequest("Somthing went wronge check IMAP or SMTP details and try again.");
            }

            var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
            var userAgent = Request.Headers["User-Agent"].ToString();
            var browserName = EmailTrackingHelper.GetBrowserName(userAgent);

            int userId = int.Parse(ClientId);

            // ?? FIX: execution strategy wrap
            var strategy = _context.Database.CreateExecutionStrategy();

            return await strategy.ExecuteAsync(async () =>
            {
                // ?? STEP 2: ATOMIC DB TRANSACTION
                using var transaction = await _context.Database.BeginTransactionAsync();

                try
                {
                    // Update existing record if IsUpdate = true
                    if (dto.IsUpdate && existingRecord != null)
                    {
                        existingRecord.Server = dto.OutgoingServer;
                        existingRecord.Port = dto.OutgoingPort;
                        existingRecord.Username = dto.Username;
                        existingRecord.Password = dto.Password;
                        existingRecord.SenderName = dto.SenderName;
                        existingRecord.SecurityType = dto.OutgoingSecurityType;

                        _context.SmtpCredentials.Update(existingRecord);
                        await _context.SaveChangesAsync();
                        await transaction.CommitAsync();

                        return (IActionResult)Ok("SMTP credentials updated successfully.");
                    }

                    // Create new record
                    var result = await _repo.AddEmailForDomain(
                        userId,
                        dto.FromEmail,
                        dto,
                        ipAddress,
                        browserName
                    );

                    if (!result.Success)
                    {
                        await transaction.RollbackAsync();
                        return (IActionResult)BadRequest(result.Message);
                    }

                    await _context.SaveChangesAsync();
                    await transaction.CommitAsync();

                    return (IActionResult)Ok("SMTP verified. OTP sent for domain verification.");
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync();
                    return (IActionResult)BadRequest(ex.Message);
                }
            });
        }


        [HttpGet("get-Outboxs")]
        public async Task<IActionResult> GetUsernameConfigDropdown([FromQuery] string clientId)
        {
            try
            {
                int.TryParse(clientId, out int clientIdInt);

                // ?? SMTP DATA
                var smtpList = await _context.SmtpCredentials
                    .Where(s => s.ClientId == clientId)
                    .Select(s => new
                    {
                        s.Id,
                        Email = s.Username,
                        Type = "SMTP"
                    })
                    .ToListAsync();

                // ?? OAUTH DATA
                var oauthList = await _context.EmailOAuthTokens
                    .Where(o => o.ClientId == clientIdInt)
                    .Select(o => new
                    {
                        o.Id,
                        o.Email,
                        Type = o.Provider // Gmail / Outlook / Zoho
                    })
                    .ToListAsync();

                // ?? MERGE BOTH
                var combinedList = smtpList.Concat(oauthList).ToList();

                if (!combinedList.Any())
                {
                    return NotFound(new
                    {
                        success = false,
                        message = "No email configurations found for this client."
                    });
                }

                return Ok(new
                {
                    success = true,
                    message = "Email configurations fetched successfully.",
                    data = combinedList
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    success = false,
                    message = "An unexpected error occurred.",
                    detail = ex.Message
                });
            }
        }


        [HttpGet("campaign-outboxs")]
        public async Task<IActionResult> GetCampaignOutboxDropdown([FromQuery] string clientId, [FromQuery] int? campaignId = null)
        {
            try
            {
                int.TryParse(clientId, out int clientIdInt);

                List<int>? usedOutboxIds = null;

                if (campaignId.HasValue)
                {
                    usedOutboxIds = await _context.EmailLogs
                        .Where(log => log.ClientId == clientIdInt
                            && log.CampaignId == campaignId.Value
                            && log.outboxid != null
                            && log.process_name != "ThreadReply")
                        .Select(log => log.outboxid!.Value)
                        .Distinct()
                        .ToListAsync();
                }

                var smtpQuery = _context.SmtpCredentials
                    .Where(s => s.ClientId == clientId);

                if (usedOutboxIds != null)
                {
                    smtpQuery = smtpQuery.Where(s => usedOutboxIds.Contains(s.Id));
                }

                var smtpList = await smtpQuery
                    .Select(s => new
                    {
                        s.Id,
                        Email = s.Username,
                        Type = "SMTP"
                    })
                    .ToListAsync();

                var oauthQuery = _context.EmailOAuthTokens
                    .Where(o => o.ClientId == clientIdInt);

                if (usedOutboxIds != null)
                {
                    oauthQuery = oauthQuery.Where(o => usedOutboxIds.Contains(o.Id));
                }

                var oauthList = await oauthQuery
                    .Select(o => new
                    {
                        o.Id,
                        o.Email,
                        Type = o.Provider
                    })
                    .ToListAsync();

                var combinedList = smtpList.Concat(oauthList).ToList();

                if (!combinedList.Any())
                {
                    return NotFound(new
                    {
                        success = false,
                        message = campaignId.HasValue
                            ? "No sender configurations found for this campaign."
                            : "No email configurations found for this client."
                    });
                }

                return Ok(new
                {
                    success = true,
                    message = campaignId.HasValue
                        ? "Campaign sender configurations fetched successfully."
                        : "Email configurations fetched successfully.",
                    data = combinedList
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    success = false,
                    message = "An unexpected error occurred.",
                    detail = ex.Message
                });
            }
        }        [HttpGet("get-sequence")]
        public async Task<IActionResult> GetSequenceSteps([FromQuery] string ClientId)
        {
            if (string.IsNullOrWhiteSpace(ClientId))
                return BadRequest(new { message = "ClientId is required." });

            try
            {
                var clientIdInt = Convert.ToInt32(ClientId);

                var steps = await _context.SequenceSteps
                    .Where(s => s.ClientId == clientIdInt)
                    .Where(s =>
                        s.Title != null &&
                        s.ScheduledDate != null &&
                        s.ScheduledTime != null &&
                        s.TimeZone != null &&
                        s.SmtpID != null)
                    .ToListAsync();

                if (steps == null || steps.Count == 0)
                    return NotFound(new { message = "No valid sequence steps found for this client." });

                var result = steps.Select(s =>
                {
                    // Combine the stored UTC date and time
                    var utcDateTime = s.ScheduledDate.Date + s.ScheduledTime;

                    // Default values for display
                    string displayDate = s.ScheduledDate.ToString("dd MMM yyyy");
                    string displayTime = s.ScheduledTime.ToString(@"hh\:mm\:ss");

                    try
                    {
                        // Try to convert back to client's timezone for display
                        if (!string.IsNullOrEmpty(s.TimeZone))
                        {
                            TimeZoneInfo clientTimeZone = TimeZoneInfo.FindSystemTimeZoneById(s.TimeZone);
                            var localDateTime = TimeZoneInfo.ConvertTimeFromUtc(utcDateTime, clientTimeZone);

                            displayDate = localDateTime.ToString("dd MMM yyyy");
                            displayTime = localDateTime.ToString("HH:mm:ss");
                        }
                    }
                    catch (TimeZoneNotFoundException)
                    {
                        // If timezone conversion fails, use UTC values
                        // Log this error in production
                    }

                    return new
                    {
                        s.Id,
                        s.ClientId,
                        s.Title,
                        s.BccEmail,
                        // Return the display dates/times (in user's timezone)
                        ScheduledDate = displayDate,  // Now returns "21 May 2025" format
                        ScheduledTime = displayTime,
                        s.TimeZone,
                        s.SmtpID,
                        s.Provider,
                        s.zohoviewName,
                        s.IsSent,
                        s.TestIsSent,
                        s.DataFileId,
                        s.SegmentId,
                        // Optionally include UTC times for debugging
                        UtcScheduledDateTime = utcDateTime.ToString("dd MMM yyyy HH:mm:ss") + " UTC"
                    };
                });

                return Ok(result);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "An unexpected error occurred.", detail = ex.Message });
            }
        }
        //[HttpGet("get-sequence")]
        //public async Task<IActionResult> GetSequenceSteps([FromQuery] string ClientId)
        //{
        //    if (string.IsNullOrWhiteSpace(ClientId))
        //        return BadRequest(new { message = "ClientId is required." });

        //    try
        //    {
        //        var clientIdInt = Convert.ToInt32(ClientId);

        //        var steps = await (
        //            from step in _context.SequenceSteps

        //                // LEFT JOIN to ZohoViewDetails
        //            join zoho in _context.zohoViewIddetails
        //                on step.zohoviewName equals zoho.zohoviewId into zohoJoin
        //            from zoho in zohoJoin.DefaultIfEmpty()

        //                // LEFT JOIN to SmtpCredential
        //            join smtp in _context.SmtpCredentials
        //                on step.SmtpID equals smtp.Id into smtpJoin
        //            from smtp in smtpJoin.DefaultIfEmpty()

        //            where step.ClientId == clientIdInt
        //&& step.Title != null
        //&& step.Emailsubject != null
        //&& step.ScheduledDate != null
        //&& step.ScheduledTime != null
        //&& step.TimeZone != null
        //&& step.SmtpID != null

        //            select new
        //            {
        //                step.Id,
        //                step.ClientId,
        //                step.Title,
        //                step.Emailsubject,
        //                step.BccEmail,
        //                step.ScheduledDate,
        //                step.ScheduledTime,
        //                step.TimeZone,
        //                SmtpName = smtp != null ? smtp.Username : null,
        //                ZohoViewName = zoho != null ? zoho.zohoviewName : null,
        //                step.IsSent,
        //                step.TestIsSent
        //            }
        //        ).ToListAsync();

        //        if (steps == null || steps.Count == 0)
        //            return NotFound(new { message = "No valid sequence steps found for this client." });

        //        return Ok(steps);
        //    }
        //    catch (Exception ex)
        //    {
        //        return StatusCode(500, new { message = "An unexpected error occurred.", detail = ex.Message });
        //    }
        //}

        [HttpPost("update-sequence/{id:int}")]
        public async Task<IActionResult> UpdateSequence(int id, [FromQuery] string ClientId, [FromBody] SequenceCreateDto dto)
        {
            if (string.IsNullOrWhiteSpace(ClientId))
                return BadRequest(new { message = "ClientId is required." });

            if (dto == null)
                return BadRequest(new { message = "Request body is missing or invalid." });

            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            try
            {
                var clientIdInt = Convert.ToInt32(ClientId);

                var step = await _context.SequenceSteps.FirstOrDefaultAsync(s => s.Id == id && s.ClientId == clientIdInt);
                if (step == null)
                    return NotFound(new { message = "Sequence step not found for this client." });

                // Validate TimeZone
                TimeZoneInfo clientTimeZone;
                try
                {
                    clientTimeZone = TimeZoneInfo.FindSystemTimeZoneById(dto.TimeZone);
                }
                catch (TimeZoneNotFoundException)
                {
                    return BadRequest(new { message = $"Invalid TimeZone ID: {dto.TimeZone}" });
                }

                var outboxExists = await SequenceOutboxExistsAsync(dto.Provider, dto.SmtpID, ClientId);

                if (!outboxExists)
                    return BadRequest(new { message = $"Invalid {dto.Provider} outbox ID: {dto.SmtpID} for this client." });

                // Convert and update date & time
                var localDateTime = dto.Steps[0].ScheduledDate.Date + dto.Steps[0].ScheduledTime;
                var utcDateTime = TimeZoneInfo.ConvertTimeToUtc(localDateTime, clientTimeZone);

                // Update fields
                step.Title = dto.Title?.Trim() ?? string.Empty;
                step.ScheduledDate = utcDateTime.Date;
                step.ScheduledTime = utcDateTime.TimeOfDay;
                step.TimeZone = dto.TimeZone;
                step.zohoviewName = dto.zohoviewName?.Trim() ?? string.Empty;
                step.DataFileId = dto.DataFileId ?? step.DataFileId; // Keep existing if null
                step.BccEmail = dto.BccEmail;
                step.SmtpID = dto.SmtpID;
                step.Provider = dto.Provider;
                step.DataFileId = dto.DataFileId;


                _context.SequenceSteps.Update(step);
                await _context.SaveChangesAsync();

                return Ok(new { message = "Sequence step updated successfully." });
            }
            catch (DbUpdateException dbEx)
            {
                return StatusCode(500, new { message = "Database error occurred.", detail = dbEx.Message });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "An unexpected error occurred.", detail = ex.Message });
            }
        }

        [HttpPost("delete-sequence/{id:int}")]
        public async Task<IActionResult> DeleteSequence(int id, [FromQuery] string ClientId)
        {
            if (string.IsNullOrWhiteSpace(ClientId))
                return BadRequest(new { message = "ClientId is required." });

            try
            {
                var clientIdInt = Convert.ToInt32(ClientId);

                // Step 1: Get all steps with given sequence ID & client
                var stepsToDelete = await _context.SequenceSteps
                    .Where(s => s.Id == id && s.ClientId == clientIdInt)
                    .ToListAsync();

                if (stepsToDelete == null || stepsToDelete.Count == 0)
                    return NotFound(new { message = "Sequence not found for this client." });

                // Step 2: Remove the steps
                _context.SequenceSteps.RemoveRange(stepsToDelete);
                await _context.SaveChangesAsync();

                return Ok(new { message = "Sequence deleted successfully." });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    message = "An unexpected error occurred.",
                    detail = ex.Message,
                    inner = ex.InnerException?.Message
                });
            }
        }

        [HttpGet("bcc-emails")]
        public async Task<IActionResult> GetBccEmails([FromQuery] string clientId)
        {
            // Step 1: Validate & Convert
            if (!int.TryParse(clientId, out int clientIdInt))
            {
                return Ok(new List<string>());
            }

            var bccEmails = await _context.SequenceSteps
                .Where(s => s.ClientId == clientIdInt && !string.IsNullOrEmpty(s.BccEmail))
                .Select(s => s.BccEmail)
                .Distinct()
                .ToListAsync();

            return Ok(bccEmails);
        }


        [HttpGet("success-count")]
        public async Task<IActionResult> GetSuccessCount([FromQuery] string clientId)
        {
            if (!int.TryParse(clientId, out int parsedClientId))
                return BadRequest("Valid clientId is required.");

            int count = await _context.EmailLogs
                .Where(e => e.IsSuccess == true &&
                            e.ClientId == parsedClientId)
                .CountAsync();

            return Ok(count);
        }

        [HttpPost("{clinteId}")]
        public async Task<IActionResult> AddBccEmail(int clinteId, [FromBody] BccEmailDto dto)
        {
            if (string.IsNullOrWhiteSpace(dto.BccEmailAddress))
                return BadRequest("BccEmail is required.");

            // Check duplicate
            bool exists = _context.BccEmail.Any(x =>
                x.BccEmailAddress == dto.BccEmailAddress && x.ClinteId == clinteId);

            if (exists)
                return Conflict("This BCC email already exists for this client.");

            // Insert into DB
            var entity = new BccEmail
            {
                BccEmailAddress = dto.BccEmailAddress,
                ClinteId = clinteId
            };

            _context.BccEmail.Add(entity);
            await _context.SaveChangesAsync();

            return Ok(new { message = "BccEmail saved successfully", data = entity });
        }

        [HttpGet("get-by-clinte")]
        public async Task<IActionResult> GetBccEmailsByClinteId([FromQuery] int clinteId)
        {
            var emails = await _context.BccEmail
                .Where(b => b.ClinteId == clinteId)
                .Select(b => new
                {
                    b.Id,
                    b.BccEmailAddress,
                    b.ClinteId
                })
                .ToListAsync();

            if (emails == null || emails.Count == 0)
            {
                return NotFound($"No BccEmails found for ClinteId {clinteId}.");
            }

            return Ok(emails);
        }


        [HttpPost("delete")]
        public async Task<IActionResult> DeleteBccEmail([FromQuery] int id, [FromQuery] int clinteId)
        {
            var bccEmail = await _context.BccEmail
                .FirstOrDefaultAsync(b => b.Id == id && b.ClinteId == clinteId);

            if (bccEmail == null)
            {
                return NotFound($"No record found for Id={id} and ClinteId={clinteId}");
            }

            _context.BccEmail.Remove(bccEmail);
            await _context.SaveChangesAsync();

            return Ok(new { message = $"Record with Id={id} and ClinteId={clinteId} deleted successfully." });
        }


        [HttpPost("update")]
        public async Task<IActionResult> UpdateBccEmail([FromQuery] int id, [FromQuery] int clinteId, [FromQuery] string bccEmail)
        {
            var record = await _context.BccEmail
                .FirstOrDefaultAsync(b => b.Id == id && b.ClinteId == clinteId);

            if (record == null)
            {
                return NotFound($"No BccEmail found for Id={id} and ClinteId={clinteId}");
            }

            record.BccEmailAddress = bccEmail;
            await _context.SaveChangesAsync();

            return Ok(new { message = $"BccEmail updated to '{bccEmail}' for Id={id}, ClinteId={clinteId}" });
        }

        private async Task<bool> SequenceOutboxExistsAsync(string provider, int outboxId, string clientId)
        {
            if (!int.TryParse(clientId, out int clientIdInt))
                return false;

            if (provider == "Gmail" || provider == "Outlook")
            {
                return await _context.EmailOAuthTokens.AnyAsync(o =>
                    o.Id == outboxId &&
                    o.ClientId == clientIdInt &&
                    o.Provider.ToUpper() == provider.ToUpper());
            }

            return await _context.SmtpCredentials.AnyAsync(s =>
                s.Id == outboxId &&
                s.ClientId == clientId);
        }
    }
}


