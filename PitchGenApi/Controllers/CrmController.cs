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

                // Step 2: Get related contacts
                var contactsToDelete = _context.contacts
                    .Where(c => c.DataFileId == dataFileId);

                int deletedContacts = await contactsToDelete.CountAsync();

                // Step 3: Delete contacts
                _context.contacts.RemoveRange(contactsToDelete);

                // Step 4: Delete data file
                _context.data_files.Remove(dataFile);

                await _context.SaveChangesAsync();

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
                    c.email_sent_at
                })
                .ToListAsync();

            return Ok(new
            {
                contactCount = contacts.Count,
                contacts
            });
        }



        [HttpPost("contacts/update-email")]
        public async Task<IActionResult> UpdateContactEmail([FromBody] ContactEmailUpdateDto request)
        {
            if (request.ClientId <= 0 || request.DataFileId <= 0 || request.ContactId <= 0)
                return BadRequest("ClientId, DataFileId, and ContactId are required.");

            var dataFile = await _context.data_files
                .FirstOrDefaultAsync(df => df.id == request.DataFileId && df.client_id == request.ClientId);

            if (dataFile == null)
                return NotFound("Data file not found for this client.");

            var contact = await _context.contacts
                .FirstOrDefaultAsync(c => c.id == request.ContactId && c.DataFileId == request.DataFileId);

            if (contact == null)
                return NotFound("Contact not found for this data file.");

            // ✅ null ya empty string dono skip honge
            if (!string.IsNullOrWhiteSpace(request.EmailSubject))
                contact.email_subject = request.EmailSubject;

            if (!string.IsNullOrWhiteSpace(request.EmailBody))
                contact.email_body = request.EmailBody;

            contact.updated_at = DateTime.UtcNow;

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
                    .ToListAsync();

                return Ok(result); // 🔁 Returns full list of DataFile objects
            }
            catch (Exception ex)
            {
                return BadRequest("Error: " + ex.Message);
            }
        }
        [HttpGet("getlogs")]
        public async Task<IActionResult> GetLogs([FromQuery] int clientId, [FromQuery] int dataFileId)
        {
            // Step 1: Validate that the dataFile belongs to this client
            bool isValid = await _context.data_files
                .AnyAsync(df => df.id == dataFileId && df.client_id == clientId);

            if (!isValid)
            {
                return BadRequest("Invalid clientId or dataFileId.");
            }

            // Step 2: Fetch EmailLogs directly using the model
            var logs = await _context.EmailLogs
                .Where(e => e.ClientId == clientId && e.DataFileId == dataFileId)
                .OrderByDescending(e => e.SentAt)
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

        [HttpPost("Creat-Segments")]
        public async Task<IActionResult> CreateSegment([FromQuery] int ClientId, [FromBody] CreateSegmentDto dto)
        {
            var dataFileExists = await _context.data_files
                .AnyAsync(df => df.id == dto.DataFileId && df.client_id == ClientId);

            if (!dataFileExists)
            {
                return BadRequest(new { message = "Invalid ClientId or DataFileId. No matching data file found." });
            }

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

            foreach (var contactId in dto.ContactIds)
            {
                var segmentContact = new SegmentContact
                {
                    SegmentId = segment.Id,
                    ContactId = contactId,
                    AddedAt = DateTime.UtcNow
                };
                _context.segmentContacts.Add(segmentContact);
            }

            await _context.SaveChangesAsync();

            return Ok(new { message = "Segment created successfully", segmentId = segment.Id });
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
                    s.UpdatedAt
                })
                .ToListAsync();

            if (segments == null || segments.Count == 0)
            {
                return NotFound(new { message = "No segments found for this client." });
            }

            return Ok(segments);
        }

        [HttpGet("segment/{segmentId}/contacts")]
        public IActionResult GetContactsBySegmentId(int segmentId)
        {
            var contacts = _context.segmentContacts
                .Where(sc => sc.SegmentId == segmentId)
                .Include(sc => sc.Contact)
                .Select(sc => sc.Contact)
                .ToList();

            return Ok(contacts);
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

    }
}
