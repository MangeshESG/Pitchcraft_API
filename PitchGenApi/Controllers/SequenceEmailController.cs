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

namespace PitchGenApi.Controllers
{
    [ApiController]
    [Route("api/email")]
    public class SequenceEmailController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly ContactRepository _contactRepository;
        private readonly EmailSendingHelper _emailHelper;


        public SequenceEmailController(AppDbContext context, ContactRepository contactRepository, EmailSendingHelper emailHelper)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _contactRepository = contactRepository;
            _emailHelper = emailHelper;
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
            catch (Exception)
            {
                return BadRequest(new { message = "Error processing TimeZone." });
            }

            //  Check if the provided SmtpID is valid for this ClientId
            var smtpExists = await _context.SmtpCredentials
                .AnyAsync(s => s.Id == dto.SmtpID && s.ClientId == ClientId);

            if (!smtpExists)
            {
                return BadRequest(new
                {
                    message = $"Invalid SMTP ID: {dto.SmtpID}. No SMTP configuration found for this client."
                });
            }

            // Time for creation
            var now = DateTime.Now;
            var newSteps = new List<SequenceStep>();

            try
            {
                foreach (var step in dto.Steps)
                {
                    var localDateTime = step.ScheduledDate.Date + step.ScheduledTime;
                    var utcDateTime = TimeZoneInfo.ConvertTimeToUtc(localDateTime, clientTimeZone);

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
                        DataFileId = dto.DataFileId,
                        TestIsSent = false,
                        SmtpID = dto.SmtpID,
                        IsSent = true
                    };

                    newSteps.Add(entity);
                }

                await _context.SequenceSteps.AddRangeAsync(newSteps);
                await _context.SaveChangesAsync();

                return Ok(new { message = $"{newSteps.Count} sequence step(s) saved successfully." });
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
                Server = dto.Server,
                Port = dto.Port,
                Username = dto.Username,
                Password = dto.Password,
                FromEmail = dto.FromEmail,
                UseSsl = dto.UseSsl
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
                var smtp = await _context.SmtpCredentials.FirstOrDefaultAsync(s => s.Id == id && s.ClientId == ClientId);
                if (smtp == null)
                    return NotFound("SMTP credentials not found.");

                // Update existing values
                smtp.Server = dto.Server;
                smtp.Port = dto.Port;
                smtp.Username = dto.Username;
                smtp.Password = dto.Password;
                smtp.FromEmail = dto.FromEmail;
                smtp.UseSsl = dto.UseSsl;

                _context.SmtpCredentials.Update(smtp);
                await _context.SaveChangesAsync();

                return Ok(new { message = "SMTP credentials updated successfully." });
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

        [HttpPost("delete-smtp/{id:int}")]
        public async Task<IActionResult> DeleteSmtp(int id, [FromQuery] string ClientId)
        {
            if (string.IsNullOrWhiteSpace(ClientId))
                return BadRequest("ClientId is required.");

            try
            {
                // Step 1: SMTP record get karo without accessing any properties directly
                var smtp = await _context.SmtpCredentials
                    .Where(s => s.Id == id && s.ClientId == ClientId)
                    .FirstOrDefaultAsync();

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
                var smtpList = await _context.SmtpCredentials
                    .Where(s => s.ClientId == ClientId)
                    // Yahan hum null check kar rahe hain ki koi bhi required column null na ho
                    .Where(s => s.Server != null
                                && s.Username != null
                                && s.Password != null
                                && s.FromEmail != null)
                    .ToListAsync();

                if (smtpList == null || smtpList.Count == 0)
                    return NotFound("No SMTP credentials found for this client.");

                var result = smtpList.Select(smtp => new
                {
                    smtp.Id,
                    smtp.ClientId,
                    smtp.Server,
                    smtp.Port,
                    smtp.Username,
                    smtp.Password,
                    smtp.UseSsl,
                    smtp.FromEmail
                });

                return Ok(result);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "An unexpected error occurred.", detail = ex.Message });
            }
        }


        [HttpPost("send-singleEmail")]
        public async Task<IActionResult> SendSingleEmail([FromQuery] int clientId, [FromQuery] int dataFileId, [FromQuery] int? contactId = null, [FromQuery] int smtpId = 0, [FromQuery] string bccEmail = null)
        {
            try
            {
                ServicePointManager.ServerCertificateValidationCallback = (sender, certificate, chain, sslPolicyErrors) => true;

                // Get contact with optional next
                var contactWithNext = await _contactRepository.GetContactWithNextAsync(dataFileId, contactId);
                if (contactWithNext == null || contactWithNext.CurrentContact == null || string.IsNullOrWhiteSpace(contactWithNext.CurrentContact.email))
                    return BadRequest("No valid contact found for the given DataFileId and ContactId.");

                var contact = contactWithNext.CurrentContact;

                // Basic values
                string toEmail = contact.email;
                string subject = contact.email_subject ?? "No Subject";
                string rawBody = contact.email_body ?? "No Content";
                string body = string.IsNullOrWhiteSpace(rawBody) ? "No content provided." : rawBody;

                // Send email using SMTP
                var success = await _emailHelper.SendEmailUsingSmtp(
                    clientId,
                    dataFileId,
                    toEmail,
                    subject,
                    body,
                    bccEmail,
                    smtpId,
                    dataFileId.ToString(),
                    contact.full_name,
                    contact.country_or_address,
                    contact.company_name,
                    contact.website,
                    contact.linkedin_url,
                    contact.job_title
                );

                if (!success)
                    return StatusCode(500, "Failed to send email. Please try again later.");

                return Ok(new
                {
                    message = $"Email sent successfully to {toEmail}.",
                    contactName = contact.full_name,
                    currentContactId = contact.id,
                    nextContactId = contactWithNext.NextContactId
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Unexpected error: {ex.Message}");
            }
        }

        [HttpPost("configTestMail")]
        public async Task<IActionResult> configTestMail([FromQuery] string ClientId, [FromBody] SmtpCredentialDto dto)
        {
            try
            {
                //var clientIdStr = User.FindFirst("UserId")?.Value;
                //if (string.IsNullOrEmpty(clientIdStr) || !int.TryParse(clientIdStr, out int clientId))
                //    return Unauthorized("Invalid or missing client ID.");
                if (string.IsNullOrEmpty(ClientId))
                    return NotFound("Data not found");
                string toEmail = "info@mailtester.co.uk";
                string body = @"
                    <!DOCTYPE html>
                    <html>
                    <head>
                      <meta charset='UTF-8'>
                      <title>Business Contact Information Notice</title>
                    </head>
                    <body style='font-family: Arial, sans-serif; color: #333; line-height: 1.6; padding: 20px;'>
                      <p>Hi,</p>
                      <p>I hope this email finds you well.</p>
                      <p>Please review this email regarding the collection and processing of your business contact information by <strong>Mailtester</strong>.</p>
                      <p>Mailtester employs a skilled team of researchers whose aim is to source business contact information for professionals working in selected corporations, companies, and organisations.</p>
                      <p>The information collected is:</p>
                      <ul>
                        <li>Name</li>
                        <li>Corporation/Company/Organisation Name</li>
                        <li>Business Phone Number</li>
                        <li>Business Email Address</li>
                        <li>Job Title</li>
                        <li>Job Function and Responsibilities</li>
                      </ul>
                      <p>This information is collected via various publicly available sources and/or by in-person request.</p>
                      <p><strong>Sensitive personal information/data</strong> such as date of birth, personal email address, personal phone number, or government identification number is <strong>NEVER</strong> collected or stored by Mailtester. We only source business-related information.</p>
                    </body>
                    </html>";
                string subject = "Information notice on business data processing";

                var smtp = new SmtpCredentials
                {
                    ClientId = ClientId,
                    Server = dto.Server,
                    Port = dto.Port,
                    Username = dto.Username,
                    Password = dto.Password,
                    FromEmail = dto.FromEmail,
                    UseSsl = dto.UseSsl
                };


                using var smtpClient = new SmtpClient(smtp.Server)
                {
                    Port = smtp.Port,
                    Credentials = new NetworkCredential(smtp.Username, smtp.Password),
                    EnableSsl = true,
                };

                using var mailMessage = new MailMessage
                {
                    From = new MailAddress(smtp.Username),
                    Subject = subject,
                    Body = body,
                    IsBodyHtml = true,
                };

                mailMessage.To.Add(toEmail);

                await smtpClient.SendMailAsync(mailMessage);

                // Log success
                _context.EmailLogs.Add(new EmailLog
                {
                    ClientId = Convert.ToInt32(ClientId),
                    ToEmail = toEmail,
                    Subject = subject,
                    Body = body,
                    IsSuccess = true,
                    SentAt = DateTime.UtcNow
                });

                await _context.SaveChangesAsync();

                return Ok(new
                {
                    message = $"Testing email sent successfully to {toEmail}.",
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"An unexpected error occurred: {ex.Message}");
            }
        }


        [HttpGet("get-SMTPUser")]
        public async Task<IActionResult> GetUsernameConfigDropdown([FromQuery] string ClientId)
        {
            try
            {
                var usernameList = await _context.SmtpCredentials
                    .Where(s => s.ClientId == ClientId)
                    .Select(s => new
                    {
                        Id = s.Id,
                        Username = s.Username ?? "N/A"
                    })
                    .ToListAsync();

                if (usernameList == null || !usernameList.Any())
                {
                    return NotFound(new
                    {
                        success = false,
                        message = "No SMTP users found for this client. Please ensure SMTP credentials are configured."
                    });
                }

                return Ok(new
                {
                    success = true,
                    message = "SMTP user(s) fetched successfully.",
                    data = usernameList
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

        [HttpGet("get-sequence")]
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

                var result = steps.Select(s => new
                {
                    s.Id,
                    s.ClientId,
                    s.Title,
                    s.BccEmail,
                    s.ScheduledDate,
                    s.ScheduledTime,
                    s.TimeZone,
                    s.SmtpID,
                    s.zohoviewName,
                    s.IsSent,
                    s.TestIsSent,
                    s.DataFileId
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

                // Validate SMTP ID
                var smtpExists = await _context.SmtpCredentials
                    .AnyAsync(s => s.Id == dto.SmtpID && s.ClientId == ClientId);

                if (!smtpExists)
                    return BadRequest(new { message = $"Invalid SMTP ID: {dto.SmtpID} for this client." });

                // Convert and update date & time
                var localDateTime = dto.Steps[0].ScheduledDate.Date + dto.Steps[0].ScheduledTime;
                var utcDateTime = TimeZoneInfo.ConvertTimeToUtc(localDateTime, clientTimeZone);

                // Update fields
                step.Title = dto.Title?.Trim() ?? string.Empty;
                step.ScheduledDate = utcDateTime.Date;
                step.ScheduledTime = utcDateTime.TimeOfDay;
                step.TimeZone = dto.TimeZone;
                step.zohoviewName = dto.zohoviewName?.Trim() ?? string.Empty;
                step.BccEmail = dto.BccEmail;
                step.SmtpID = dto.SmtpID;
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


    }
}