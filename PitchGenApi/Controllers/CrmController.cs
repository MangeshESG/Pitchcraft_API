using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PitchGenApi.Database;
using PitchGenApi.DTOs;
using PitchGenApi.Models;
using System;
using System.Linq;
using System.Threading.Tasks;
using PitchGenApi.Model.DTOs;
using PitchGenApi.Model;


namespace PitchGenApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class CrmController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly ContactRepository _repository;

        public CrmController(AppDbContext context)
        {
            _context = context;
            _repository = new ContactRepository(context);

        }

        [HttpPost("uploadcontacts")]
        public async Task<IActionResult> UploadContacts([FromBody] DataFileWithContactsDto request)
        {
            using var transaction = await _context.Database.BeginTransactionAsync();

            try
            {
                var dataFile = new DataFile
                {
                    client_id = request.clientId,
                    name = request.name,
                    data_file_name = request.dataFileName,
                    description = request.description,
                    created_at = DateTime.UtcNow
                };

                _context.data_files.Add(dataFile);
                await _context.SaveChangesAsync();

                var contacts = request.contacts.Select(c => new Contact
                {
                    DataFileId = dataFile.id,
                    full_name = c.fullName,
                    email = c.email,
                    website = c.website,
                    company_name = c.companyName,
                    job_title = c.jobTitle,
                    linkedin_url = c.linkedInUrl,
                    country_or_address = c.countryOrAddress,
                    email_subject = c.emailSubject,
                    email_body = c.emailBody,
                    CompanyTelephone = c.CompanyTelephone,
                    CompanyEmployeeCount = c.CompanyEmployeeCount,
                    CompanyIndustry = c.CompanyIndustry,
                    CompanyLinkedInURL = c.CompanyLinkedInURL,
                    CompanyEventLink = c.CompanyEventLink,
                    created_at = DateTime.UtcNow,
                    updated_at = null
                }).ToList();

                _context.contacts.AddRange(contacts);
                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                return Ok(new
                {
                    success = true,
                    message = "Contacts uploaded successfully",
                    dataFileId = dataFile.id,
                    contactCount = contacts.Count
                });
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                return BadRequest(new
                {
                    success = false,
                    message = "Upload failed",
                    error = ex.InnerException?.Message ?? ex.Message
                });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetContacts([FromQuery] int? dataFileId)
        {
            var contacts = await _repository.GetContactsAsync(dataFileId);
            return Ok(contacts);
        }
        [HttpGet("Singel-contact")]
        public async Task<IActionResult> GetContactWithNext([FromQuery] int dataFileId, [FromQuery] int? contactId = null)
        {
            if (dataFileId == 0)
                return BadRequest("dataFileId is required.");

            var result = await _repository.GetContactWithNextAsync(dataFileId, contactId);

            if (result == null)
                return NotFound("Contact not found.");

            return Ok(result);
        }

        [HttpPost("delete-contacts-and-file")]
        public async Task<IActionResult> DeleteContactsAndFile([FromQuery] int clientId, [FromQuery] int dataFileId)
        {
            try
            {
                // Step 1: Check if data_file exists
                var dataFile = await _context.data_files
                    .FirstOrDefaultAsync(df => df.id == dataFileId && df.client_id == clientId);

                if (dataFile == null)
                {
                    return NotFound("Data file not found for the given client.");
                }

                // Step 2: Delete related contacts in bulk (fast)
                // Changed DataFileId to data_file_id (or whatever your actual column name is)
                int deletedContacts = await _context.Database.ExecuteSqlInterpolatedAsync(
                    $"DELETE FROM contacts WHERE data_file_id = {dataFileId}");

                // Step 3: Delete the data file
                _context.data_files.Remove(dataFile);
                await _context.SaveChangesAsync();

                // Step 4: Return result
                return Ok(new
                {
                    Message = $"Deleted {deletedContacts} contacts and data file ID {dataFileId} successfully."
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = "Internal server error", Error = ex.Message });
            }
        }


        [HttpGet("contacts/by-client-datafile")]
        public async Task<IActionResult> GetContactsByClientAndDataFileId([FromQuery] int clientId, [FromQuery] int dataFileId)
        {
            if (clientId <= 0 || dataFileId <= 0)
                return BadRequest("Both clientId and dataFileId must be greater than 0.");

            // Check if data file exists and belongs to this client
            var dataFileExists = await _context.data_files
                .AnyAsync(df => df.id == dataFileId && df.client_id == clientId);

            if (!dataFileExists)
                return NotFound("No data file found for this client.");

            // Fetch contacts for that data file
            var contacts = await _context.contacts
                  .Where(c => c.DataFileId == dataFileId)
                  .OrderBy(c => c.id)
                  .Select(c => new
                  {
                      c.id,
                      c.full_name,
                      c.email,
                      c.website,
                      c.company_name,
                      c.job_title,
                      c.linkedin_url,
                      c.country_or_address,
                      c.email_subject,
                      c.email_body,
                      c.created_at,
                      c.updated_at,
                      c.email_sent_at,
                      c.CompanyTelephone,
                      c.CompanyEmployeeCount,
                      c.CompanyIndustry,
                      c.CompanyLinkedInURL,
                      c.CompanyEventLink
                  })
                  .ToListAsync();


            return Ok(new
            {
                contactCount = contacts.Count,
                contacts
            });
        }



        [HttpPost("update-datafile")]
        public async Task<IActionResult> UpdateDataFileById([FromQuery] int id, [FromQuery] string name, [FromQuery] string description, [FromQuery] string dataFileName)
        {
            if (id == 0)
                return BadRequest("Invalid id");

            var dataFile = await _context.data_files
                .FirstOrDefaultAsync(d => d.id == id);

            if (dataFile == null)
                return NotFound("Data file not found");

            dataFile.name = name;
            dataFile.description = description;
            dataFile.data_file_name = dataFileName;
            dataFile.updated_at = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            return Ok("Data file updated successfully");
        }



        [HttpPost("contacts/update-email")]
        public async Task<IActionResult> UpdateContactEmail([FromBody] ContactEmailUpdateDto request)
        {
            if (request.ClientId <= 0 || request.ContactId <= 0)
                return BadRequest("ClientId and ContactId are required.");

            // Verify contact belongs to the client
            var contact = await _context.contacts
                .Include(c => c.data_file)
                .FirstOrDefaultAsync(c => c.id == request.ContactId && c.data_file.client_id == request.ClientId);

            if (contact == null)
                return NotFound("Contact not found for this client.");

            // Update fields if provided
            if (!string.IsNullOrWhiteSpace(request.EmailSubject))
                contact.email_subject = request.EmailSubject;

            if (!string.IsNullOrWhiteSpace(request.EmailBody))
                contact.email_body = request.EmailBody;

            contact.updated_at = DateTime.UtcNow;

            // 🧠 Deduct credit only from FinalUserCredit when GPTGenerate = true
            if (request.GPTGenerate == true)
            {
                var finalCredit = await _context.FinalUserCredit
                .FirstOrDefaultAsync(f => f.ClientId == request.ClientId);

                if (finalCredit != null)
                {
                    // Case 1: Use TotalCredit if available and monthly limit not reached
                    if ((finalCredit.TotalCredit ?? 0) > 0 && (finalCredit.LimitUsed ?? 0) < (finalCredit.MonthlyLimit ?? 0))
                    {
                        finalCredit.TotalCredit -= 1;
                        finalCredit.UsedCredit = (finalCredit.UsedCredit ?? 0) + 1;
                        finalCredit.LimitUsed = (finalCredit.LimitUsed ?? 0) + 1;
                    }
                    // Case 2: Use CustomLimit when monthly limit is reached or TotalCredit is 0
                    else if ((finalCredit.CustomLimit ?? 0) > 0)
                    {
                        finalCredit.CustomLimit -= 1;
                        finalCredit.CustomCreditUsed = (finalCredit.CustomCreditUsed ?? 0) + 1;

                        // 🔹 Also deduct from latest active UserCredits plan
                        var latestActivePlan = await _context.UserCredits
                            .Where(u => u.ClientId == request.ClientId &&
                                        u.Status.ToLower() == "active" &&
                                        u.Plane == "Custom Credit")
                            .OrderByDescending(u => u.CreatedAt)
                            .FirstOrDefaultAsync();

                        if (latestActivePlan != null && latestActivePlan.Credits > 0)
                        {
                            latestActivePlan.Credits -= 1;
                            _context.UserCredits.Update(latestActivePlan);
                        }
                    }

                    finalCredit.UpdatedAt = DateTime.UtcNow;
                    _context.FinalUserCredit.Update(finalCredit);
                    await _context.SaveChangesAsync();
                }


            }

            await _context.SaveChangesAsync();

            return Ok(new
            {
                success = true,
                message = "Contact email subject/body updated successfully.",
                contactId = contact.id
            });
        }

        [HttpGet("datafile-byclientid")]
        public async Task<IActionResult> GetDataFilesByClientId(int clientId)
        {
            try
            {
                var result = await _context.data_files
                    .Where(x => x.client_id == clientId)
                    .Select(x => new
                    {
                        x.id,
                        x.client_id,
                        x.name,
                        x.data_file_name,
                        x.description,
                        x.created_at,
                        x.updated_at,

                        // 👇 Only contact count
                        contactCount = _context.contacts
                            .Count(c => c.DataFileId == x.id)
                    })
                    .ToListAsync();

                return Ok(result);
            }
            catch (Exception ex)
            {
                return BadRequest("Error: " + ex.Message);
            }
        }

        [HttpGet("getlogs")]
        public async Task<IActionResult> GetLogs([FromQuery] int clientId, [FromQuery] int dataFileId)
        {
            // Step 1: Validate clientId + dataFileId match
            bool isValid = await _context.data_files
                .AnyAsync(df => df.id == dataFileId && df.client_id == clientId);

            if (!isValid)
                return BadRequest("Invalid clientId or dataFileId.");

            // Step 2: Fetch EmailLogs with Contact details (anonymous projection)
            var logs = await (
                from log in _context.EmailLogs
                join contact in _context.contacts
                on log.ContactId equals contact.id into contactGroup
                from contact in contactGroup.DefaultIfEmpty() // left join (some ContactId might be null)
                where log.ClientId == clientId && log.DataFileId == dataFileId
                orderby log.SentAt descending
                select new
                {
                    // Email log details
                    log.Id,
                    log.ContactId,
                    log.ClientId,
                    log.DataFileId,
                    log.Subject,
                    log.Body,
                    log.SentAt,
                    log.IsSuccess,
                    log.ErrorMessage,
                    log.ToEmail,
                    log.process_name,

                    // Contact details
                    Name = contact.full_name,
                    Email = contact.email,
                    address = contact.country_or_address,
                    Website = contact.website,
                    Company = contact.company_name,
                    JobTitle = contact.job_title,
                    LinkedIn = contact.linkedin_url
                }
            )
            .Take(1000)
            .ToListAsync();

            return Ok(logs);
        }

        [HttpGet("gettrackinglogs")]
        public async Task<IActionResult> GettrackingLogs([FromQuery] int clientId, [FromQuery] int dataFileId)
        {
            // Step 1: Validate dataFileId belongs to clientId
            bool isValid = await _context.data_files
                .AnyAsync(df => df.id == dataFileId && df.client_id == clientId);

            if (!isValid)
            {
                return BadRequest("Invalid clientId or dataFileId.");
            }

            // Step 2: Get EmailTrackingLogs
            var logs = await _context.EmailTrackingLogs
                .Where(e => e.ClientId == clientId && e.DataFileId == dataFileId)
                .OrderByDescending(e => e.Timestamp)
                .Take(1000)
                .ToListAsync();

            return Ok(logs);
        }

        [HttpGet("getlogs-by-segment")]
        public async Task<IActionResult> GetLogsBySegment([FromQuery] int clientId, [FromQuery] int segmentId)
        {
            // Step 1: Validate segmentId belongs to clientId
            bool isValid = await _context.segments
                .AnyAsync(s => s.Id == segmentId && s.ClientId == clientId);

            if (!isValid)
                return BadRequest("Invalid clientId or segmentId.");

            // Step 2: Use a join approach
            var logs = await (
                from log in _context.EmailLogs
                join sc in _context.segmentContacts on log.ContactId equals sc.ContactId
                join contact in _context.contacts on log.ContactId equals contact.id into contactGroup
                from contact in contactGroup.DefaultIfEmpty()
                where sc.SegmentId == segmentId && log.ClientId == clientId
                orderby log.SentAt descending
                select new
                {
                    // Email log details
                    log.Id,
                    log.ContactId,
                    log.ClientId,
                    log.DataFileId,
                    log.Subject,
                    log.Body,
                    log.SentAt,
                    log.IsSuccess,
                    log.ErrorMessage,
                    log.ToEmail,
                    log.process_name,

                    // Contact details
                    Name = contact.full_name,
                    Email = contact.email,
                    Address = contact.country_or_address,
                    Website = contact.website,
                    Company = contact.company_name,
                    JobTitle = contact.job_title,
                    LinkedIn = contact.linkedin_url
                }
            )
            .Take(1000)
            .ToListAsync();

            return Ok(logs);
        }

        [HttpGet("gettrackinglogs-by-segment")]
        public async Task<IActionResult> GetTrackingLogsBySegment([FromQuery] int clientId, [FromQuery] int segmentId)
        {
            // Step 1: Validate segmentId belongs to clientId
            bool isValid = await _context.segments
                .AnyAsync(s => s.Id == segmentId && s.ClientId == clientId);

            if (!isValid)
                return BadRequest("Invalid clientId or segmentId.");

            // Step 2: Use a join approach
            var logs = await (
                from log in _context.EmailTrackingLogs
                join sc in _context.segmentContacts on log.ContactId equals sc.ContactId
                where sc.SegmentId == segmentId && log.ClientId == clientId
                orderby log.Timestamp descending
                select log
            )
            .Take(1000)
            .ToListAsync();

            return Ok(logs);
        }

        [HttpPost("Creat-Segments")]
        public async Task<IActionResult> CreateSegment([FromQuery] int ClientId, [FromBody] CreateSegmentDto dto)
        {
            try
            {
                // Validate input
                if (dto.ContactIds == null || !dto.ContactIds.Any())
                {
                    return BadRequest(new { message = "ContactIds cannot be empty" });
                }

                // Validate DataFile exists
                var dataFileExists = await _context.data_files
                    .AnyAsync(df => df.id == dto.DataFileId && df.client_id == ClientId);

                //if (!dataFileExists)
                //{
                //    return BadRequest(new { message = "Invalid ClientId or DataFileId. No matching data file found." });
                //}

                // Create a comma-separated list of contact IDs for SQL query
                var contactIdsList = dto.ContactIds.Distinct().ToList();
                var contactIdsString = string.Join(",", contactIdsList);

                // Use FromSqlRaw to avoid EF Core query translation issues
                var existingContactIds = await _context.contacts
                    .FromSqlRaw($"SELECT * FROM contacts WHERE id IN ({contactIdsString})")
                    .Select(c => c.id)
                    .ToListAsync();

                var invalidContactIds = contactIdsList.Except(existingContactIds).ToList();

                // Check if there are any valid contacts
                if (!existingContactIds.Any())
                {
                    return BadRequest(new
                    {
                        message = "None of the provided contacts exist. Segment not created.",
                        invalidContactCount = invalidContactIds.Count,
                        invalidContactIds = invalidContactIds
                    });
                }

                // Create segment with valid contacts only
                var segment = new Segment
                {
                    Name = dto.Name,
                    ClientId = ClientId,
                    Description = dto.Description,
                    DataFileId = dto.DataFileId,
                    CreatedAt = DateTime.UtcNow
                };

                _context.segments.Add(segment);
                await _context.SaveChangesAsync();

                // Batch insert segment contacts (only valid ones)
                var segmentContacts = existingContactIds.Select(contactId => new SegmentContact
                {
                    SegmentId = segment.Id,
                    ContactId = contactId,
                    AddedAt = DateTime.UtcNow
                }).ToList();

                _context.segmentContacts.AddRange(segmentContacts);
                await _context.SaveChangesAsync();

                // Prepare response message
                var message = invalidContactIds.Any()
                    ? $"Segment created successfully. {invalidContactIds.Count} invalid contact(s) found and skipped. {existingContactIds.Count} contact(s) added successfully."
                    : "Segment created successfully";

                var response = new
                {
                    message = message,
                    segmentId = segment.Id,
                    contactsAdded = existingContactIds.Count,
                    contactsRequested = contactIdsList.Count,
                    validContactCount = existingContactIds.Count,
                    invalidContactCount = invalidContactIds.Count
                };

                // Add invalid contact IDs to response if any exist
                if (invalidContactIds.Any())
                {
                    return Ok(new
                    {
                        response.message,
                        response.segmentId,
                        response.contactsAdded,
                        response.contactsRequested,
                        response.validContactCount,
                        response.invalidContactCount,
                        invalidContactIds = invalidContactIds
                    });
                }

                return Ok(response);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    message = "An error occurred while creating the segment",
                    error = ex.Message,
                    innerError = ex.InnerException?.Message
                });
            }
        }


        [HttpGet("get-segments-by-client")]
        public async Task<IActionResult> GetSegmentsByClientId([FromQuery] int clientId)
        {
            var segments = await _context.segments
                .Where(s => s.ClientId == clientId)
                .Select(s => new
                {
                    s.Id,
                    s.Name,
                    s.Description,
                    s.DataFileId,
                    s.ClientId,
                    s.CreatedAt,
                    s.UpdatedAt,

                    // 👇 Count contacts mapped to this segment
                    contactCount = _context.segmentContacts
                        .Count(c => c.SegmentId == s.Id)
                })
                .ToListAsync();

            if (segments == null || segments.Count == 0)
            {
                return NotFound(new { message = "No segments found for this client." });
            }

            return Ok(segments);
        }

        [HttpPost("delete-Datafile-contact")]
        public async Task<IActionResult> DeleteContact([FromQuery] int contactId)
        {
            try
            {
                var contact = await _context.contacts
                    .FirstOrDefaultAsync(c => c.id == contactId);

                if (contact == null)
                {
                    return NotFound(new { message = "Contact not found." });
                }

                _context.contacts.Remove(contact);
                await _context.SaveChangesAsync();

                return Ok(new { message = "Contact deleted successfully." });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("delete-by-segment")]
        public async Task<IActionResult> DeleteByContactIds([FromQuery] int contactId)
        {
            if (contactId <= 0)
                return BadRequest("contactId is required");

            try
            {
                var contacts = await _context.segmentContacts
                    .Where(c => c.ContactId == contactId)
                    .ToListAsync();

                if (!contacts.Any())
                {
                    return NotFound(new { message = "Contact not found." });
                }

                _context.segmentContacts.RemoveRange(contacts);
                await _context.SaveChangesAsync();

                return Ok(new { message = "Contact deleted successfully.", deletedCount = contacts.Count });
            }
            catch (Exception ex)
            {
                return StatusCode(500, ex.Message);
            }
        }



        [HttpGet("segment/{segmentId}/contacts")]
        public async Task<IActionResult> GetContactsBySegmentId(int segmentId)
        {
            var contacts = await _repository.GetContactBySegment(segmentId);
            return Ok(contacts);
        }


        [HttpPost("update-segment")]
        public async Task<IActionResult> UpdateSegmentById([FromQuery] int id, [FromQuery] string name, [FromQuery] string? description)
        {
            if (id == 0)
                return BadRequest("Invalid Segment Id");

            var segment = await _context.segments
                .FirstOrDefaultAsync(s => s.Id == id);

            if (segment == null)
                return NotFound("Segment not found");

            segment.Name = name;
            segment.Description = description;
            segment.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            return Ok("Segment updated successfully");
        }

        [HttpPost("delete-segment")]
        public async Task<IActionResult> DeleteSegment([FromQuery] int segmentId)
        {
            using var transaction = await _context.Database.BeginTransactionAsync();

            try
            {
                // Step 1: Fetch the Segment (null-safe, no properties accessed)
                var segment = await _context.segments
                                            .FirstOrDefaultAsync(s => s.Id == segmentId);

                if (segment == null)
                {
                    return NotFound(new { message = "Segment not found." });
                }

                // Step 2: Get SegmentContacts list (null-safe)
                var segmentContacts = await _context.segmentContacts
                                                    .Where(sc => sc.SegmentId == segmentId)
                                                    .ToListAsync();

                // Step 3: Remove related contacts
                if (segmentContacts?.Count > 0)
                {
                    _context.segmentContacts.RemoveRange(segmentContacts);
                }

                // Step 4: Remove the Segment
                _context.segments.Remove(segment);

                // Save all changes
                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                return Ok(new { message = "Segment and related contacts deleted successfully." });
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                return StatusCode(500, new
                {
                    message = "Internal server error",
                    error = ex.InnerException?.Message ?? ex.Message
                });
            }
        }


        [HttpGet("user_credit")]
        public async Task<IActionResult> GetCredit([FromQuery] int clientId)
        {
            if (clientId <= 0)
            {
                return BadRequest("clientId is required");
            }

            try
            {
                var credit = await _context.FinalUserCredit
                    .FirstOrDefaultAsync(x => x.ClientId == clientId);

                if (credit == null)
                {
                    return Ok(new
                    {
                        total = 0,
                        canGenerate = false,
                        monthlyLimitExceeded = false
                    });
                }

                // 🧩 Calculate total available credit
                var total = (credit.TotalCredit ?? 0) + (credit.CustomLimit ?? 0);

                // 🧠 Logic for "can generate"
                bool canGenerate = true;
                bool monthlyLimitExceeded = false;

                // 📌 Case 1: No credits at all
                if ((credit.TotalCredit ?? 0) == 0 && (credit.CustomLimit ?? 0) == 0)
                {
                    canGenerate = false;
                }

                // 📌 Case 2: Monthly limit reached and no custom credits left
                if ((credit.LimitUsed ?? 0) >= (credit.MonthlyLimit ?? 0) && (credit.CustomLimit ?? 0) == 0)
                {
                    canGenerate = false;
                    monthlyLimitExceeded = true;
                }

                return Ok(new
                {
                    total,
                    canGenerate,
                    monthlyLimitExceeded
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Internal server error: {ex.Message}");
            }
        }

        [HttpGet("Check_credit")]
        public async Task<IActionResult> CheckCredit([FromQuery] int clientId)
        {
            if (clientId <= 0)
            {
                return BadRequest("clientId is required");
            }

            try
            {
                var total = await _context.FinalUserCredit
                  .Where(x => x.ClientId == clientId)
                  .Select(x => new
                  {
                      x.TotalCredit,
                      x.MonthlyLimit,
                      x.LimitUsed,
                      x.CustomCreditUsed,
                      x.CustomLimit
                  })
                  .FirstOrDefaultAsync();


                if (total == null)
                {
                    return NotFound($"No credit found for client with ID {clientId}");
                }

                return Ok(total);
            }
            catch (Exception ex)
            {
                // Log the exception or handle it based on your requirements
                return StatusCode(500, $"Internal server error: {ex.Message}");
            }
        }

        [HttpGet("get-tone-settings")]
        public async Task<IActionResult> GetToneSettings([FromQuery] int clientId)
        {
            if (clientId <= 0)
                return BadRequest("Invalid clientId");

            try
            {
                var settings = await _context.ToneSettings
                    .FirstOrDefaultAsync(ts => ts.ClientId == clientId);

                if (settings == null)
                {
                    // Return default settings if none exist
                    return Ok(new ToneSettingsDto
                    {
                        Language = "English",
                        SubjectTemplate = "",
                        Emojis = "None",
                        Tone = "Professional",
                        ChattyLevel = "Medium",
                        CreativityLevel = "Medium",
                        ReasoningLevel = "Medium",
                        DateGreeting = "No",
                        DateFarewell = "No"
                    });
                }

                return Ok(new ToneSettingsDto
                {
                    Language = settings.Language,
                    SubjectTemplate = settings.SubjectTemplate,
                    Emojis = settings.Emojis,
                    Tone = settings.Tone,
                    ChattyLevel = settings.ChattyLevel,
                    CreativityLevel = settings.CreativityLevel,
                    ReasoningLevel = settings.ReasoningLevel,
                    DateGreeting = settings.DateGreeting,
                    DateFarewell = settings.DateFarewell
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Internal server error", error = ex.Message });
            }
        }

        [HttpPost("save-tone-settings")]
        public async Task<IActionResult> SaveToneSettings([FromQuery] int clientId, [FromBody] ToneSettingsDto dto)
        {
            if (clientId <= 0)
                return BadRequest("Invalid clientId");

            if (dto == null)
                return BadRequest("Settings data is required");

            try
            {
                var existingSettings = await _context.ToneSettings
                    .FirstOrDefaultAsync(ts => ts.ClientId == clientId);

                if (existingSettings != null)
                {
                    // Update existing settings
                    existingSettings.Language = dto.Language ?? "English";
                    existingSettings.SubjectTemplate = dto.SubjectTemplate ?? "";
                    existingSettings.Emojis = dto.Emojis ?? "None";
                    existingSettings.Tone = dto.Tone ?? "Professional";
                    existingSettings.ChattyLevel = dto.ChattyLevel ?? "Medium";
                    existingSettings.CreativityLevel = dto.CreativityLevel ?? "Medium";
                    existingSettings.ReasoningLevel = dto.ReasoningLevel ?? "Medium";
                    existingSettings.DateGreeting = dto.DateGreeting ?? "No";
                    existingSettings.DateFarewell = dto.DateFarewell ?? "No";
                    existingSettings.UpdatedAt = DateTime.UtcNow;

                    await _context.SaveChangesAsync();

                    return Ok(new
                    {
                        success = true,
                        message = "Settings updated successfully",
                        settingsId = existingSettings.Id
                    });
                }
                else
                {
                    // Create new settings
                    var newSettings = new ToneSettings
                    {
                        ClientId = clientId,
                        Language = dto.Language ?? "English",
                        SubjectTemplate = dto.SubjectTemplate ?? "",
                        Emojis = dto.Emojis ?? "None",
                        Tone = dto.Tone ?? "Professional",
                        ChattyLevel = dto.ChattyLevel ?? "Medium",
                        CreativityLevel = dto.CreativityLevel ?? "Medium",
                        ReasoningLevel = dto.ReasoningLevel ?? "Medium",
                        DateGreeting = dto.DateGreeting ?? "No",
                        DateFarewell = dto.DateFarewell ?? "No",
                        CreatedAt = DateTime.UtcNow
                    };

                    _context.ToneSettings.Add(newSettings);
                    await _context.SaveChangesAsync();

                    return Ok(new
                    {
                        success = true,
                        message = "Settings created successfully",
                        settingsId = newSettings.Id
                    });
                }
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Internal server error", error = ex.Message });
            }
        }

    }
}
