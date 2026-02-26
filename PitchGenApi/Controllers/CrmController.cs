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
using System.Text;


namespace PitchGenApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class CrmController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly ContactRepository _contactRepository;

        public CrmController(AppDbContext context, ContactRepository contactRepository)
        {
            _context = context;
            _contactRepository = contactRepository;

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
                    //CompanyEventLink = c.CompanyEventLink,
                    linkedIninformation = c.linkedIninformation,
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
        [HttpPost("add-single-contact")]
        public async Task<IActionResult> AddSingleContact([FromQuery] int DataFileId, ContactDto request)
        {
            using var transaction = await _context.Database.BeginTransactionAsync();

            try
            {
                // Check DataFile exist karti hai ya nahi
                var dataFile = await _context.data_files
                    .FirstOrDefaultAsync(df => df.id == DataFileId);

                if (dataFile == null)
                {
                    return NotFound(new
                    {
                        success = false,
                        message = "DataFile not found"
                    });
                }

                // Create contact from DTO
                var contact = new Contact
                {
                    DataFileId = DataFileId,
                    full_name = request.fullName,
                    email = request.email,
                    website = request.website,
                    company_name = request.companyName,
                    job_title = request.jobTitle,
                    linkedin_url = request.linkedInUrl,
                    country_or_address = request.countryOrAddress,
                    email_subject = request.emailSubject,
                    email_body = request.emailBody,
                    CompanyTelephone = request.CompanyTelephone,
                    CompanyEmployeeCount = request.CompanyEmployeeCount,
                    CompanyIndustry = request.CompanyIndustry,
                    CompanyLinkedInURL = request.CompanyLinkedInURL,
                    //CompanyEventLink = request.CompanyEventLink,
                    created_at = DateTime.UtcNow,
                    updated_at = null,
                    linkedIninformation = request.linkedIninformation
                };

                _context.contacts.Add(contact);
                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                return Ok(new
                {
                    success = true,
                    message = "Contact added successfully",
                    dataFileId = DataFileId,
                    contactId = contact.id
                });
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();

                return BadRequest(new
                {
                    success = false,
                    message = "Failed to add contact",
                    error = ex.InnerException?.Message ?? ex.Message
                });
            }
        }

        [HttpPost]
        [Route("update-contact")]
        public async Task<IActionResult> UpdateContact([FromQuery]int id ,[FromBody] ContactDto model)
        {
            try
            {
                var contact = await _context.contacts
                    .FirstOrDefaultAsync(x => x.id == id);

                if (contact == null)
                {
                    return NotFound(new { message = "Contact not found" });
                }

                // Update fields
                contact.full_name = model.fullName;
                contact.email = model.email;
                contact.job_title = model.jobTitle;
                contact.website = model.website;
                contact.linkedin_url = model.linkedInUrl;
                contact.company_name = model.companyName;
                contact.company_name = model.companyName;
                contact.company_name = model.companyName;
                contact.country_or_address = model.countryOrAddress;
                contact.email_subject = model.emailSubject;
                contact.email_body = model.emailBody;
                contact.CompanyTelephone = model.CompanyTelephone;
                contact.CompanyLinkedInURL = model.CompanyLinkedInURL;
                contact.CompanyIndustry = model.CompanyIndustry;
                contact.CompanyEmployeeCount = model.CompanyEmployeeCount;
                contact.linkedIninformation = model.linkedIninformation;
                contact.updated_at = DateTime.UtcNow;

                await _context.SaveChangesAsync();

                return Ok(new { message = "Contact updated successfully" });
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }

        [HttpPost]
        [Route("Update-linkedIninformation")]
        public async Task<IActionResult> UpdateNotes([FromQuery] int contactid, [FromBody] string linkedIninformation)
        {
            try
            {
                var contact = await _context.contacts
                    .FirstOrDefaultAsync(x => x.id == contactid);

                if (contact == null)
                {
                    return NotFound(new { message = "Contact not found" });
                }

                // Update fields
                contact.linkedIninformation = linkedIninformation;
                contact.updated_at = DateTime.UtcNow;

                await _context.SaveChangesAsync();

                return Ok(new { message = "Notes updated successfully" });
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetContacts([FromQuery] int? dataFileId)
        {
            var contacts = await _contactRepository.GetContactsAsync(dataFileId);
            return Ok(contacts);
        }
        [HttpGet("Singel-contact")]
        public async Task<IActionResult> GetContactWithNext([FromQuery] int dataFileId, [FromQuery] int? contactId = null)
        {
            if (dataFileId == 0)
                return BadRequest("dataFileId is required.");

            var result = await _contactRepository.GetContactWithNextAsync(dataFileId, contactId);

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
        public async Task<IActionResult> GetContactsByClientAndDataFileId([FromQuery] int clientId, [FromQuery] int dataFileId, [FromQuery] bool isFollowUp)
        {
            if (clientId <= 0 || dataFileId <= 0)
                return BadRequest("Both clientId and dataFileId must be greater than 0.");

            var dataFileExists = await _context.data_files
                .AnyAsync(df => df.id == dataFileId && df.client_id == clientId);

            if (!dataFileExists)
                return NotFound("No data file found for this client.");

            // Fetch contacts except unsubscribed
            var contacts = await _context.contacts
                .Where(c => c.DataFileId == dataFileId &&
                    !_context.UnsubscribedContacts
                        .Any(uc => uc.ClientId == clientId && uc.Email == c.email))
                .OrderBy(c => c.id)
                .ToListAsync();
            // Yaha follow-up logic apply hoga
            var result = new List<object>();

            foreach (var c in contacts)
            {
                string finalEmailBody = c.email_body;

                if (isFollowUp)
                {

                    if (c.updated_at < c.email_sent_at)
                    {
                        c.email_body = "You have not krafted any email after sending the last email. Please kraft to continue.";
                    }

                    string oldThread = await _contactRepository.BuildEmailThreadAsync(clientId, dataFileId, c.id, null);

                    finalEmailBody =
                    $@"{c.email_body}

                {oldThread}";
                }

                result.Add(new
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
                    email_body = finalEmailBody,
                    c.created_at,
                    c.updated_at,
                    c.email_sent_at,
                    c.CompanyTelephone,
                    c.CompanyEmployeeCount,
                    c.CompanyIndustry,
                    c.CompanyLinkedInURL,
                    c.linkedIninformation
                    //c.CompanyEventLink
                });
            }

            return Ok(new
            {
                contactCount = result.Count,
                contacts = result
            });
        }


        [HttpGet("contacts/List-by-CleinteId")]
        public async Task<IActionResult> GetContactsByClientAndDataFileIdList([FromQuery] int clientId, [FromQuery] int dataFileId)
        {
            try
            {
                if (clientId <= 0 || dataFileId <= 0)
                    return BadRequest(new
                    {
                        success = false,
                        message = "Both clientId and dataFileId must be greater than 0."
                    });

                var dataFileExists = await _context.data_files
                    .AnyAsync(df => df.id == dataFileId && df.client_id == clientId);

                if (!dataFileExists)
                    return NotFound(new
                    {
                        success = false,
                        message = "No data file found for this client."
                    });

                // Load unsubscribed emails
                var unsubscribedEmails = await _context.UnsubscribedContacts
                    .Where(u => u.ClientId == clientId)
                    .Select(u => u.Email)
                    .ToListAsync();

                // 1️⃣ Step 1: Load contacts from SQL (simple projection)
                var contactsRaw = await _context.contacts
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
                        //c.CompanyEventLink,
                        c.linkedIninformation
                    })
                    .ToListAsync();

                // 2️⃣ Step 2: Add unsubscribe flag in C#
                var contacts = contactsRaw
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
                        //c.CompanyEventLink,
                        c.linkedIninformation,

                        unsubscribe = unsubscribedEmails.Contains(c.email) ? "Yes" : "No"
                    })
                    .ToList();

                return Ok(new
                {
                    success = true,
                    contactCount = contacts.Count,
                    contacts
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    success = false,
                    message = "An unexpected server error occurred.",
                    error = ex.Message
                });
            }
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
              await _contactRepository.CreditDeduction(request.ClientId);
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
        public async Task<IActionResult> GettrackingLogs(int clientId, int dataFileId)
        {
            bool isValid = await _context.data_files
                .AnyAsync(df => df.id == dataFileId && df.client_id == clientId);

            if (!isValid)
            {
                return BadRequest("Invalid clientId or dataFileId.");
            }

            var logs = await (
                from t in _context.EmailTrackingLogs
                join e in _context.EmailLogs
                    on new { t.TrackingId, t.ClientId, t.DataFileId }
                    equals new { e.TrackingId, e.ClientId, e.DataFileId }
                    into emailGroup
                from e in emailGroup.DefaultIfEmpty()   // 👈 left join
                where t.ClientId == clientId && t.DataFileId == dataFileId
                orderby t.Timestamp descending
                select new
                {
                    t.Id,
                    t.Email,
                    t.EventType,
                    t.Timestamp,
                    t.ClientId,
                    t.TargetUrl,
                    t.ZohoViewName,
                    t.Full_Name,
                    t.Location,
                    t.Company,
                    t.JobTitle,
                    t.linkedin_URL,
                    t.website,
                    t.TrackingId,
                    t.UserAgent,
                    t.IPAddress,
                    t.IsBot,
                    t.Browser,
                    t.DataFileId,
                    t.ContactId,
                    t.SegmentId,
                    t.CampaignId,
                    t.BlueprintId,
                    SentAt = e != null ? e.SentAt : null   // 👈 Second table se field
                }
            )
            .Take(1000)
            .ToListAsync();

            return Ok(logs);
        }
        [HttpGet("getlogs-by-segment")]
        public async Task<IActionResult> GetLogsBySegment([FromQuery] int clientId, [FromQuery] int segmentId)
        {
            bool isValid = await _context.segments
                .AnyAsync(s => s.Id == segmentId && s.ClientId == clientId);

            if (!isValid)
                return BadRequest("Invalid clientId or segmentId.");

            var logs = await (
                from log in _context.EmailLogs
                join contact in _context.contacts
                    on log.ContactId equals contact.id into contactGroup
                from contact in contactGroup.DefaultIfEmpty()
                where log.ClientId == clientId
                      && log.SegmentId == segmentId   // 👈 Direct filter
                orderby log.SentAt descending
                select new
                {
                    log.Id,
                    log.ContactId,
                    log.ClientId,
                    log.DataFileId,
                    log.TrackingId,
                    log.SegmentId,
                    log.Subject,
                    log.Body,
                    log.SentAt,
                    log.IsSuccess,
                    log.ErrorMessage,
                    log.ToEmail,
                    log.process_name,

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
                from t in _context.EmailTrackingLogs
                join e in _context.EmailLogs
                    on new { t.TrackingId, t.ClientId, t.SegmentId }
                    equals new { e.TrackingId, e.ClientId, e.SegmentId }
                    into emailGroup
                from e in emailGroup.DefaultIfEmpty()   // 👈 left join
                where t.ClientId == clientId && t.SegmentId == segmentId
                orderby t.Timestamp descending
                select new
                {
                    t.Id,
                    t.Email,
                    t.EventType,
                    t.Timestamp,
                    t.ClientId,
                    t.TargetUrl,
                    t.ZohoViewName,
                    t.Full_Name,
                    t.Location,
                    t.Company,
                    t.JobTitle,
                    t.linkedin_URL,
                    t.website,
                    t.TrackingId,
                    t.UserAgent,
                    t.IPAddress,
                    t.IsBot,
                    t.Browser,
                    t.DataFileId,
                    t.ContactId,
                    t.SegmentId,
                    t.CampaignId,
                    t.BlueprintId,
                    SentAt = e != null ? e.SentAt : null   // 👈 Second table se field
                }
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

        [HttpGet("get-list-by-client")]
        public async Task<IActionResult> GetListByClientId([FromQuery] int clientId)
        {
            var List = await _context.data_files
                .Where(s => s.client_id == clientId)
                .Select(s => new
                {
                    s.id,
                    s.name,
                    s.description,
                    s.client_id,
                    s.created_at,

                    // 👇 Count contacts mapped to this segment
                    contactCount = _context.contacts
                        .Count(c => c.DataFileId == s.id)
                })
                .ToListAsync();

            if (List == null || List.Count == 0)
            {
                return NotFound(new { message = "No segments found for this client." });
            }

            return Ok(List);
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
            var contacts = await _contactRepository.GetContactBySegment(segmentId);
            return Ok(contacts);
        }

        [HttpGet("contacts/by-client-segment")]
        public async Task<IActionResult> GetContactsBySegmentId([FromQuery] int clientId, [FromQuery] int segmentId, [FromQuery] bool isFollowUp)
        {
            if (clientId <= 0 || segmentId <= 0)
                return BadRequest("clientId aur segmentId dono 0 se bade hone chahiye.");

            // Step 1: Check Segment exists and belongs to same client
            var segment = await _context.segments
                .FirstOrDefaultAsync(s => s.Id == segmentId && s.ClientId == clientId);

            if (segment == null)
                return NotFound("Is client ke liye segment nahi mila.");

            // Step 2: Get contact IDs from SegmentContacts
            var contactIds = await _context.segmentContacts
                .Where(sc => sc.SegmentId == segmentId)
                .Select(sc => sc.ContactId)
                .ToListAsync();

            if (!contactIds.Any())
                return Ok(new { contactCount = 0, contacts = new List<object>() });

            // Step 3: Load contacts one-by-one using foreach (SQL error fix)
            var contactsRaw = new List<Contact>();

            foreach (var cid in contactIds)
            {
                if (cid <= 0)
                    continue;

                var contact = await _context.contacts
                    .FirstOrDefaultAsync(c => c.id == cid);

                if (contact != null)
                    contactsRaw.Add(contact);
            }

            // Step 4: Load unsubscribed emails
            var unsubscribedEmails = await _context.UnsubscribedContacts
                .Where(u => u.ClientId == clientId)
                .Select(u => u.Email)
                .ToListAsync();

            var result = new List<object>();
            // Step 5: Build final response
            foreach (var c in contactsRaw)
            {
                // skip unsubscribed emails
                if (unsubscribedEmails.Contains(c.email))
                    continue;

                string finalEmailBody = c.email_body;

                // Add follow-up thread
                if (isFollowUp)
                {
                    if (c.updated_at < c.email_sent_at)
                    {
                        c.email_body = "You have not krafted any email after sending the last email. Please kraft to continue.";
                    }

                    string oldThread = await _contactRepository.BuildEmailThreadAsync(clientId, segment.DataFileId, c.id, segmentId);
                    finalEmailBody =
                    $@"{c.email_body}

                    {oldThread}";
                }

                result.Add(new
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
                    email_body = finalEmailBody,
                    c.created_at,
                    c.updated_at,
                    c.email_sent_at,
                    c.CompanyTelephone,
                    c.CompanyEmployeeCount,
                    c.CompanyIndustry,
                    c.CompanyLinkedInURL,
                    c.linkedIninformation
                    //c.CompanyEventLink
                });
            }

            return Ok(new
            {
                contactCount = result.Count,
                contacts = result
            });
        }

        [HttpGet("segment-contacts")]
        public async Task<IActionResult> GetContactsBySegmentId([FromQuery] int clientId, [FromQuery] int segmentId)
        {
            try
            {
                if (clientId <= 0 || segmentId <= 0)
                    return BadRequest(new
                    {
                        success = false,
                        message = "clientId and segmentId must be greater  than 0"
                    });

                // Step 1: Check Segment exists
                var seg = await _context.segments
                    .FirstOrDefaultAsync(x => x.Id == segmentId && x.ClientId == clientId);

                if (seg == null)
                    return NotFound(new { success = false, message = "Segment not found." });

                // Step 2: SegmentContacts → ContactId list nikaalo
                var contactIds = await _context.segmentContacts
                    .Where(sc => sc.SegmentId == segmentId)
                    .Select(sc => sc.ContactId)
                    .ToListAsync();

                if (!contactIds.Any())
                    return Ok(new { success = true, contactCount = 0, contacts = new List<object>() });

                // Step 3: Unsubscribed emails list
                var unsubscribedEmails = await _context.UnsubscribedContacts
                    .Where(u => u.ClientId == clientId)
                    .Select(u => u.Email)
                    .ToListAsync();

                // Step 4: Contacts load karo foreach ke through (id → record fetch)
                var contactsRaw = new List<Contact>();

                foreach (var cid in contactIds)
                {
                    var contact = await _context.contacts
                        .FirstOrDefaultAsync(c => c.id == cid);

                    if (contact != null)
                        contactsRaw.Add(contact);
                }


                // Step 5: unsubscribe flag add karo
                var contacts = contactsRaw
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
                        //c.CompanyEventLink,
                        c.linkedIninformation,

                        unsubscribe = unsubscribedEmails.Contains(c.email) ? "Yes" : "No"
                    })
                    .ToList();

                return Ok(new
                {
                    success = true,
                    contactCount = contacts.Count,
                    contacts
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    success = false,
                    message = "Server error .",
                    error = ex.Message
                });
            }
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

        [HttpGet("UnsubscribeContacts")]
        public async Task<IActionResult> UnsubscribeContacts([FromQuery] int ClientId, [FromQuery] string email)
        {
            var response = await _contactRepository.AddUnsubscribedAsync(ClientId, email);
            return Ok(response);
        }


        [HttpGet("email-timeline")]
        public async Task<IActionResult> GetEmailTimelineApi(int contactId)
        {
            var result = await _contactRepository.GetEmailTimeline(contactId);

            if (result == null)
                return NotFound("No email data found");

            return Ok(result);
        }

        [HttpGet("contact-details")]
        public async Task<IActionResult> GetContactDetails([FromQuery] int contactId)
        {
            if (contactId <= 0)
                return BadRequest("Invalid contactId");

            try
            {
                var contact = await _context.contacts
                    .Where(c => c.id == contactId)
                    .Select(c => new
                    {
                        // 🔹 Contact Info
                        ContactId = c.id,
                        FullName = c.full_name,
                        Email = c.email,
                        CreatedAt = c.created_at,
                        DataFileId = c.DataFileId,

                        // 🔹 DataFile Info
                        DataFile = _context.data_files
                            .Where(df => df.id == c.DataFileId)
                            .Select(df => new
                            {
                                DataFileId = df.id,
                                DataFileName = df.name,
                                CreatedAt = df.created_at
                            })
                            .FirstOrDefault(),

                        // 🔹 Segments
                        Segments = _context.segmentContacts
                            .Where(sc => sc.ContactId == contactId)
                            .Select(sc => new
                            {
                                SegmentId = sc.SegmentId,
                                SegmentName = sc.Segment.Name,
                                Description = sc.Segment.Description,
                                AddedAt = sc.AddedAt,
                            })
                            .ToList(),

                        // 🔹 Campaigns
                        Campaigns = _context.Campaigns
                            .Where(camp =>
                                camp.ZohoViewId == c.DataFileId.ToString() ||
                                _context.segmentContacts
                                    .Where(sc => sc.ContactId == contactId)
                                    .Select(sc => sc.SegmentId)
                                    .Contains(camp.SegmentId.Value))
                            .Select(camp => new
                            {
                                CampaignId = camp.Id,
                                CampaignName = camp.CampaignName,
                                Description = camp.Description,
                                CreatedAt = camp.CreatedAt,
                                TemplateId = camp.TemplateId,

                                // 🔸 Campaign Source
                                SourceType = camp.ZohoViewId == c.DataFileId.ToString()
                                    ? "DataFile"
                                    : "Segment",

                                SourceId = camp.ZohoViewId == c.DataFileId.ToString()
                                    ? c.DataFileId
                                    : camp.SegmentId,

                                SourceName = camp.ZohoViewId == c.DataFileId.ToString()
                                    ? _context.data_files
                                        .Where(df => df.id == c.DataFileId)
                                        .Select(df => df.name)
                                        .FirstOrDefault()
                                    : _context.segments
                                        .Where(s => s.Id == camp.SegmentId)
                                        .Select(s => s.Name)
                                        .FirstOrDefault(),

                                // 🔸 Template Info
                                Template = camp.TemplateId != null
                                    ? _context.CampaignTemplates
                                        .Where(t => t.Id == camp.TemplateId)
                                        .Select(t => new
                                        {
                                            TemplateId = t.Id,
                                            TemplateName = t.TemplateName,
                                            SelectedModel = t.SelectedModel,
                                            TemplateDefinitionId = t.TemplateDefinitionId,
                                            CreatedAt = t.CreatedAt
                                        })
                                        .FirstOrDefault()
                                    : null
                            })
                            .ToList()
                    })
                    .FirstOrDefaultAsync();

                if (contact == null)
                    return NotFound($"Contact with ID {contactId} not found");

                return Ok(contact);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    message = "Internal server error",
                    error = ex.Message
                });
            }
        }



        [HttpPost("add-contacts-to-existing-segment")]
        public async Task<IActionResult> AddContactsToExistingSegment([FromQuery] int ClientId, [FromQuery] int SegmentId, [FromBody] List<int> ContactIds)
        {
            try
            {
                var result = await _contactRepository.AddContactsToSegmentAsync(ClientId, SegmentId, ContactIds);

                return Ok(result);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    message = ex.Message
                });
            }
        }

        [HttpGet("allcontacts/list-by-clientId")]
        public async Task<IActionResult> GetContactsByClientId([FromQuery] int clientId)
        {
            try
            {
                if (clientId <= 0)
                    return BadRequest(new
                    {
                        success = false,
                        message = "clientId must be greater than 0."
                    });

                // 1️⃣ Get all DataFileIds for this client
                var dataFileIds = await _context.data_files
                    .Where(df => df.client_id == clientId)
                    .Select(df => df.id)
                    .ToListAsync();

                if (!dataFileIds.Any())
                    return NotFound(new
                    {
                        success = false,
                        message = "No data files found for this client."
                    });

                // 2️⃣ Load unsubscribed emails
                var unsubscribedEmails = await _context.UnsubscribedContacts
                    .Where(u => u.ClientId == clientId)
                    .Select(u => u.Email)
                    .ToListAsync();

                // ✅ Performance optimization
                var unsubscribedSet = new HashSet<string>(unsubscribedEmails);

                // 3️⃣ Load contacts from ALL data files of this client
                var contactsRaw = await _context.contacts
                    .Where(c => c.DataFileId.HasValue &&
                                dataFileIds.Contains(c.DataFileId.Value))
                    .OrderBy(c => c.id)
                    .Select(c => new
                    {
                        c.id,
                        DataFileId = c.DataFileId.Value,
                        c.full_name,
                        c.email,
                        c.website,
                        c.company_name,
                        c.job_title,
                        c.linkedin_url,
                        c.country_or_address,
                        c.created_at,
                        c.updated_at,
                        c.email_sent_at,
                        c.CompanyTelephone,
                        c.CompanyEmployeeCount,
                        c.CompanyIndustry,
                        c.CompanyLinkedInURL,
                        c.linkedIninformation
                    })
                    .ToListAsync();

                // 4️⃣ Add unsubscribe flag safely
                var contacts = contactsRaw.Select(c => new
                {
                    c.id,
                    c.DataFileId,
                    c.full_name,
                    c.email,
                    c.website,
                    c.company_name,
                    c.job_title,
                    c.linkedin_url,
                    c.country_or_address,
                    c.created_at,
                    c.updated_at,
                    c.email_sent_at,
                    c.CompanyTelephone,
                    c.CompanyEmployeeCount,
                    c.CompanyIndustry,
                    c.CompanyLinkedInURL,
                    c.linkedIninformation,
                    unsubscribe = unsubscribedSet.Contains(c.email) ? "Yes" : "No"
                }).ToList();

                return Ok(new
                {
                    success = true,
                    dataFileCount = dataFileIds.Count,
                    contactCount = contacts.Count,
                    contacts
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    success = false,
                    message = "An unexpected server error occurred.",
                    error = ex.Message
                });
            }
        }

        [HttpGet("allcontacts/count-by-clientId")]
        public async Task<IActionResult> GetContactCountByClientId([FromQuery] int clientId)
        {
            try
            {
                if (clientId <= 0)
                    return BadRequest(new
                    {
                        success = false,
                        message = "clientId must be greater than 0."
                    });

                // 1️⃣ Get all DataFileIds for this client
                var dataFileIds = await _context.data_files
                    .Where(df => df.client_id == clientId)
                    .Select(df => df.id)
                    .ToListAsync();

                if (!dataFileIds.Any())
                    return Ok(new
                    {
                        success = true,
                        contactCount = 0
                    });

                // 2️⃣ Count contacts directly (NO data loading)
                var contactCount = await _context.contacts
                    .CountAsync(c => c.DataFileId.HasValue &&
                                     dataFileIds.Contains(c.DataFileId.Value));

                return Ok(new
                {
                    success = true,
                    contactCount
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    success = false,
                    message = "An unexpected server error occurred.",
                    error = ex.Message
                });
            }
        }
        [HttpPost("clone-contact")]
        public async Task<IActionResult> CloneContact([FromQuery] int contactId)
        {
            if (contactId <= 0)
                return BadRequest(new
                {
                    success = false,
                    message = "Invalid contactId"
                });

            try
            {
                // 1️⃣ Existing contact fetch
                var existingContact = await _context.contacts
                    .FirstOrDefaultAsync(c => c.id == contactId);

                if (existingContact == null)
                    return NotFound(new
                    {
                        success = false,
                        message = "Contact not found"
                    });

                // 2️⃣ Clone contact (new object)
                var clonedContact = new Contact
                {
                    DataFileId = existingContact.DataFileId,
                    full_name = existingContact.full_name,
                    email = existingContact.email,
                    website = existingContact.website,
                    company_name = existingContact.company_name,
                    job_title = existingContact.job_title,
                    linkedin_url = existingContact.linkedin_url,
                    country_or_address = existingContact.country_or_address,
                    email_subject = existingContact.email_subject,
                    email_body = existingContact.email_body,
                    CompanyTelephone = existingContact.CompanyTelephone,
                    CompanyLinkedInURL = existingContact.CompanyLinkedInURL,
                    CompanyIndustry = existingContact.CompanyIndustry,
                    CompanyEmployeeCount = existingContact.CompanyEmployeeCount,
                    linkedIninformation = existingContact.linkedIninformation,

                    created_at = DateTime.UtcNow,
                    updated_at = DateTime.UtcNow,
                    email_sent_at = null
                };

                // 3️⃣ Save new contact
                _context.contacts.Add(clonedContact);
                await _context.SaveChangesAsync();

                // 4️⃣ Response
                return Ok(new
                {
                    success = true,
                    message = "Contact cloned successfully",
                    originalContactId = contactId,
                    newContactId = clonedContact.id,
                    dataFileId = clonedContact.DataFileId
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    success = false,
                    message = "Error while cloning contact",
                    error = ex.Message
                });
            }
        }

        [HttpPost("contacts/delete-linkedin-info")]
        public async Task<IActionResult> DeleteLinkedInInfoBulk(int contactId)
        {
            var contact = await _context.contacts
                .FirstOrDefaultAsync(x => x.id == contactId);

            if (contact == null)
                return NotFound("Contact not found");

            contact.linkedIninformation = null; // ✅ delete linkedin info
            contact.updated_at = DateTime.Now;

            await _context.SaveChangesAsync();

            return Ok("LinkedIn information deleted successfully");
        }
        //private async Task<string> BuildEmailThreadAsync(int clientId, int datafileid)
        //{
        //    var logs = await _context.EmailLogs
        //        .Where(x => x.ClientId == clientId && x.DataFileId == datafileid)
        //        .OrderByDescending(x => x.SentAt)
        //        .ToListAsync();

        //    if (!logs.Any())
        //        return "";

        //    StringBuilder sb = new StringBuilder();

        //    foreach (var log in logs)
        //    {
        //        sb.AppendLine("-----Original Message-----");
        //        //sb.AppendLine($"From: {log.FromEmail}");
        //        sb.AppendLine($"Sent: {log.SentAt}");
        //        sb.AppendLine($"To: {log.ToEmail}");
        //        sb.AppendLine($"Subject: {log.Subject}");
        //        sb.AppendLine();
        //        sb.AppendLine(log.Body);
        //        sb.AppendLine();
        //    }

        //    return sb.ToString();
        //}



    }
}
