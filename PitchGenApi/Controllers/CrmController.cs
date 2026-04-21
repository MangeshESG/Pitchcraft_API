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
using Stripe;
using System.Reflection;
using System.Text.Json;
using PitchGenApi.Helpers;



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

        [HttpPost("custom-field")]
        public async Task<IActionResult> CreateCustomField([FromBody] CreateCustomFieldDto dto)
        {
            var fieldCount = await _context.crm_custom_fields
                .CountAsync(x => x.client_id == dto.ClientId);

            if (fieldCount >= 20)
                return BadRequest("Maximum 10 custom fields allowed.");

            var field = new CrmCustomField
            {
                client_id = dto.ClientId,
                field_name = dto.FieldName,
                field_key = dto.FieldKey,
                field_type = dto.FieldType,
                options_json = dto.OptionsJson,
                created_at = DateTime.UtcNow
            };

            _context.crm_custom_fields.Add(field);
            await _context.SaveChangesAsync();

            return Ok(field);
        }

        [HttpPost("save-custom-value")]
        public async Task<IActionResult> SaveCustomValue([FromBody] SaveCustomFieldValueDto dto)
        {
            var existing = await _context.contact_custom_field_values
                .FirstOrDefaultAsync(x =>
                    x.contact_id == dto.ContactId &&
                    x.field_id == dto.FieldId);

            if (existing != null)
            {
                existing.value = dto.Value;
            }
            else
            {
                var value = new ContactCustomFieldValue
                {
                    client_id = dto.ClientId,
                    contact_id = dto.ContactId,
                    field_id = dto.FieldId,
                    value = dto.Value,
                    created_at = DateTime.UtcNow
                };

                _context.contact_custom_field_values.Add(value);
            }

            await _context.SaveChangesAsync();

            return Ok("Saved successfully");
        }

        [HttpGet("contact-custom-values")]
        public async Task<IActionResult> GetContactCustomValues(int contactId)
        {
            var values = await (
                from v in _context.contact_custom_field_values
                join f in _context.crm_custom_fields
                    on v.field_id equals f.id
                where v.contact_id == contactId
                select new
                {
                    f.field_name,
                    f.field_type,
                    v.value
                }).ToListAsync();

            return Ok(values);
        }


        [HttpGet("custom-fields")]
        public async Task<IActionResult> GetCustomFields(int clientId)
        {
            var fields = await _context.crm_custom_fields
                .Where(x => x.client_id == clientId)
                .ToListAsync();

            return Ok(fields);
        }


        [HttpPost("custom-field-rename")]
        public async Task<IActionResult> UpdateCustomField([FromBody] UpdateCustomFieldDto dto)
        {
            var field = await _context.crm_custom_fields.FindAsync(dto.Id);

            if (field == null)
                return NotFound("Field not found");

            // Only check dropdown options
            if (field.field_type == "dropdown" && !string.IsNullOrEmpty(field.options_json))
            {
                var oldOptions = System.Text.Json.JsonSerializer.Deserialize<List<string>>(field.options_json) ?? new List<string>();
                var newOptions = System.Text.Json.JsonSerializer.Deserialize<List<string>>(dto.OptionsJson ?? "[]") ?? new List<string>();

                // Find removed options
                var removedOptions = oldOptions.Except(newOptions).ToList();

                if (removedOptions.Any())
                {
                    // Check if removed options are used in contacts
                    var usedOptions = await _context.contact_custom_field_values
                        .Where(v => v.field_id == dto.Id && removedOptions.Contains(v.value))
                        .Select(v => v.value)
                        .Distinct()
                        .ToListAsync();

                    if (usedOptions.Any())
                    {
                        return BadRequest(new
                        {
                            message = "Cannot delete option because it is used in contacts",
                            usedOptions = usedOptions
                        });
                    }
                }
            }

            field.field_name = dto.FieldName;
            field.field_type = dto.FieldType;
            field.options_json = dto.OptionsJson;

            await _context.SaveChangesAsync();

            return Ok(field);
        }

        [HttpPost("custom-field-delete/{id}")]
        public async Task<IActionResult> DeleteCustomField(int id)
        {
            var field = await _context.crm_custom_fields.FindAsync(id);

            if (field == null)
                return NotFound();

            var values = _context.contact_custom_field_values
                .Where(v => v.field_id == id);

            _context.contact_custom_field_values.RemoveRange(values);
            _context.crm_custom_fields.Remove(field);

            await _context.SaveChangesAsync();

            return Ok("Field deleted");
        }

        [HttpPost("uploadcontacts")]
        public async Task<IActionResult> UploadContacts([FromBody] DataFileWithContactsDto request)
        {
            using var transaction = await _context.Database.BeginTransactionAsync();

            try
            {
                // Create DataFile
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

                // Create Contacts
                var contacts = request.contacts
                    .Select(c =>
                    {
                        var firstName = c.firstName?.Trim();
                        var lastName = c.lastName?.Trim();

                        // Auto split
                        if (string.IsNullOrEmpty(firstName) && !string.IsNullOrWhiteSpace(c.fullName))
                        {
                            var parts = c.fullName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                            firstName = parts.FirstOrDefault();
                            lastName = parts.Length > 1 ? string.Join(" ", parts.Skip(1)) : "";
                        }

                        var fullName = !string.IsNullOrWhiteSpace(c.fullName)
                            ? c.fullName.Trim()
                            : $"{firstName} {lastName}".Trim();

                        if (string.IsNullOrWhiteSpace(fullName))
                        {
                            fullName = !string.IsNullOrWhiteSpace(c.email) ? c.email : "Unknown";
                        }

                        return new Contact
                        {
                            DataFileId = dataFile.id,
                            first_name = firstName,
                            last_name = lastName,
                            full_name = fullName,
                            email = c.email?.Trim(),
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
                            linkedIninformation = c.linkedIninformation,
                            created_at = DateTime.UtcNow,
                            updated_at = null
                        };
                    })
                    .Where(c => c != null)
                    .ToList();


                _context.contacts.AddRange(contacts);
                await _context.SaveChangesAsync();

                // Load Custom Fields (Case insensitive dictionary)
                var customFieldMap = await _context.crm_custom_fields
                    .Where(f => f.client_id == request.clientId)
                    .ToDictionaryAsync(
                        f => f.field_name.ToLower(),
                        f => f.id
                    );

                var customValues = new List<ContactCustomFieldValue>();

                for (int i = 0; i < contacts.Count; i++)
                {
                    var contact = contacts[i];
                    var dto = request.contacts[i];

                    if (dto.customFields == null || dto.customFields.Count == 0)
                        continue;

                    var processedFields = new HashSet<string>();

                    foreach (var field in dto.customFields)
                    {
                        var key = field.Key.ToLower();

                        // Prevent duplicate fields
                        if (processedFields.Contains(key))
                            continue;

                        processedFields.Add(key);

                        if (string.IsNullOrWhiteSpace(field.Value))
                            continue;

                        if (!customFieldMap.TryGetValue(key, out var fieldId))
                            continue;

                        customValues.Add(new ContactCustomFieldValue
                        {
                            client_id = request.clientId,
                            contact_id = contact.id,
                            field_id = fieldId,
                            value = field.Value,
                            created_at = DateTime.UtcNow
                        });
                    }
                }

                if (customValues.Any())
                {
                    _context.contact_custom_field_values.AddRange(customValues);
                    await _context.SaveChangesAsync();
                }

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
                DataFile dataFile = null;

                // 🔹 CASE 1: DataFileId = 0 → create/find manual DataFile
                if (DataFileId <= 0)
                {
                    dataFile = await _context.data_files
                        .FirstOrDefaultAsync(df =>
                            df.client_id == request.clientId &&
                            df.name == "All manually added contacts");

                    if (dataFile == null)
                    {
                        dataFile = new DataFile
                        {
                            client_id = request.clientId,
                            name = "All manually added contacts",
                            data_file_name = "All manually added contacts",
                            created_at = DateTime.UtcNow
                        };

                        _context.data_files.Add(dataFile);
                        await _context.SaveChangesAsync();
                    }

                    DataFileId = dataFile.id;
                }
                else
                {
                    dataFile = await _context.data_files
                        .FirstOrDefaultAsync(df => df.id == DataFileId);

                    if (dataFile == null)
                    {
                        return NotFound(new
                        {
                            success = false,
                            message = "DataFile not found"
                        });
                    }
                }

                // ✅ NAME HANDLING (IMPORTANT)
                var firstName = request.firstName?.Trim();
                var lastName = request.lastName?.Trim();

                // Auto split if only fullName is provided
                if (string.IsNullOrEmpty(firstName) && !string.IsNullOrWhiteSpace(request.fullName))
                {
                    var parts = request.fullName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    firstName = parts.FirstOrDefault();
                    lastName = parts.Length > 1 ? string.Join(" ", parts.Skip(1)) : "";
                }

                // Build full name
                var fullName = !string.IsNullOrWhiteSpace(request.fullName)
                    ? request.fullName.Trim()
                    : $"{firstName} {lastName}".Trim();

                // Final fallback
                if (string.IsNullOrWhiteSpace(fullName))
                {
                    fullName = !string.IsNullOrWhiteSpace(request.email) ? request.email : "Unknown";
                }

                // 🔹 Create Contact
                var contact = new Contact
                {
                    DataFileId = DataFileId,
                    first_name = firstName,
                    last_name = lastName,
                    full_name = fullName,
                    email = request.email?.Trim(),
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
                    linkedIninformation = request.linkedIninformation,
                    created_at = DateTime.UtcNow,
                    updated_at = null
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
        public async Task<IActionResult> UpdateContact([FromQuery] int id, [FromBody] ContactDto model)
        {
            try
            {
                var contact = await _context.contacts
                    .FirstOrDefaultAsync(x => x.id == id);

                if (contact == null)
                {
                    return NotFound(new { message = "Contact not found" });
                }

                // ✅ NAME HANDLING (IMPORTANT)
                var firstName = model.firstName?.Trim();
                var lastName = model.lastName?.Trim();

                // Auto split if only fullName is provided
                if (string.IsNullOrEmpty(firstName) && !string.IsNullOrWhiteSpace(model.fullName))
                {
                    var parts = model.fullName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    firstName = parts.FirstOrDefault();
                    lastName = parts.Length > 1 ? string.Join(" ", parts.Skip(1)) : "";
                }

                // Build full name
                var fullName = !string.IsNullOrWhiteSpace(model.fullName)
                    ? model.fullName.Trim()
                    : $"{firstName} {lastName}".Trim();

                // Final fallback
                if (string.IsNullOrWhiteSpace(fullName))
                {
                    fullName = !string.IsNullOrWhiteSpace(model.email) ? model.email : "Unknown";
                }

                // 🔹 Update base fields
                contact.first_name = firstName;
                contact.last_name = lastName;
                contact.full_name = fullName;

                contact.email = model.email?.Trim();
                contact.job_title = model.jobTitle;
                contact.website = model.website;
                contact.linkedin_url = model.linkedInUrl;
                contact.company_name = model.companyName;
                contact.country_or_address = model.countryOrAddress;
                //contact.email_subject = model.emailSubject;
                //contact.email_body = model.emailBody;
                contact.CompanyTelephone = model.CompanyTelephone;
                contact.CompanyLinkedInURL = model.CompanyLinkedInURL;
                contact.CompanyIndustry = model.CompanyIndustry;
                contact.CompanyEmployeeCount = model.CompanyEmployeeCount;
                contact.updated_at = DateTime.UtcNow;

                await _context.SaveChangesAsync();

                // ===============================
                // CUSTOM FIELD SAVE / UPDATE
                // ===============================

                if (model.customFields != null && model.customFields.Any())
                {
                    var fieldDefs = await _context.crm_custom_fields
                        .Where(f => f.client_id == model.clientId)
                        .ToDictionaryAsync(f => f.field_name, f => f);

                    var existingValues = await _context.contact_custom_field_values
                        .Where(v => v.contact_id == id)
                        .ToListAsync();

                    foreach (var field in model.customFields)
                    {
                        if (!fieldDefs.TryGetValue(field.Key, out var fieldDef))
                            continue;

                        var value = field.Value?.ToString();

                        var existing = existingValues
                            .FirstOrDefault(v => v.field_id == fieldDef.id);

                        if (existing != null)
                        {
                            existing.value = value;
                        }
                        else
                        {
                            _context.contact_custom_field_values.Add(
                                new ContactCustomFieldValue
                                {
                                    contact_id = id,
                                    client_id = model.clientId,
                                    field_id = fieldDef.id,
                                    value = value,
                                    created_at = DateTime.UtcNow
                                });
                        }
                    }

                    await _context.SaveChangesAsync();
                }

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
                var dataFile = await _context.data_files
                    .FirstOrDefaultAsync(df => df.id == dataFileId && df.client_id == clientId);

                if (dataFile == null)
                {
                    return NotFound("Data file not found for the given client.");
                }

                // 1️⃣ Delete custom field values first
                await _context.Database.ExecuteSqlInterpolatedAsync($@"
            DELETE ccfv
            FROM contact_custom_field_values ccfv
            INNER JOIN contacts c ON c.id = ccfv.contact_id
            WHERE c.data_file_id = {dataFileId}
        ");

                // 2️⃣ Delete contacts
                int deletedContacts = await _context.Database.ExecuteSqlInterpolatedAsync($@"
            DELETE FROM contacts
            WHERE data_file_id = {dataFileId}
        ");

                // 3️⃣ Delete data file
                _context.data_files.Remove(dataFile);
                await _context.SaveChangesAsync();

                return Ok(new
                {
                    Message = $"Deleted {deletedContacts} contacts and data file ID {dataFileId} successfully."
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    Message = "Internal server error",
                    Error = ex.Message
                });
            }
        }

        [HttpGet("contacts/by-client-datafile")]
        public async Task<IActionResult> GetContactsByClientAndDataFileId([FromQuery] ContactFilterDto request)
        {
            if (request.ClientId <= 0 || request.DataFileId <= 0)
                return BadRequest("Both clientId and dataFileId must be greater than 0.");

            var dataFileExists = await _context.data_files
                .AnyAsync(df => df.id == request.DataFileId && df.client_id == request.ClientId);

            if (!dataFileExists)
                return NotFound("No data file found for this client.");

            // Base query
            var query = _context.contacts
                .Where(c => c.DataFileId == request.DataFileId &&
                    !_context.UnsubscribedContacts
                        .Any(uc => uc.ClientId == request.ClientId && uc.Email == c.email));

            // ✅ Filter: Not Krafted
            if (request.NotKrafted)
            {
                query = query.Where(c => c.updated_at == null);
            }

            // ✅ Filter: Krafted but Not Sent
            if (request.KraftedNotSent)
            {
                query = query.Where(c => c.updated_at != null && c.email_sent_at == null);
            }

            var contacts = await query
                .OrderBy(c => c.id)
                .ToListAsync();

            var result = new List<object>();

            foreach (var c in contacts)
            {
                string finalEmailBody = c.email_body;

                if (request.IsFollowUp)
                {
                    if (c.updated_at < c.email_sent_at)
                    {
                        finalEmailBody = "You have not krafted any email after sending the last email. Please kraft to continue.";
                    }

                    string oldThread = await _contactRepository
                        .BuildEmailThreadAsync(request.ClientId, request.DataFileId, c.id, null);

                    finalEmailBody = $@"{finalEmailBody}

                    {oldThread}";
                }

                result.Add(new
                {
                    c.id,
                    c.full_name,
                    c.first_name,
                    c.last_name,
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
                });
            }

            return Ok(new
            {
                contactCount = result.Count,
                contacts = result
            });
        }

        [HttpGet("contacts/List-by-CleinteId")]
        [HttpGet("contacts/List-by-ClientId")]
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

                var unsubscribedEmails = await _context.UnsubscribedContacts
                    .Where(u => u.ClientId == clientId)
                    .Select(u => u.Email)
                    .ToListAsync();

                var unsubscribedSet = new HashSet<string>(
                    unsubscribedEmails.Where(x => !string.IsNullOrWhiteSpace(x)),
                    StringComparer.OrdinalIgnoreCase
                );

                var contactsRaw = await _context.contacts
                    .Where(c => c.DataFileId == dataFileId)
                    .OrderBy(c => c.id)
                    .Select(c => new
                    {
                        c.id,
                        c.full_name,
                        c.first_name,
                        c.last_name,
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
                        c.linkedIninformation
                    })
                    .ToListAsync();

                if (!contactsRaw.Any())
                {
                    return Ok(new
                    {
                        success = true,
                        contactCount = 0,
                        contacts = new List<object>()
                    });
                }

                var contactIds = contactsRaw.Select(c => c.id).ToList();

                var notesContactIds = await _context.Notes
                    .Where(n => n.ClientId == clientId)
                    .Select(n => n.ContactId)
                    .Distinct()
                    .ToListAsync();

                var notesSet = new HashSet<int>(notesContactIds);

                var customValues = await (
                    from v in _context.contact_custom_field_values
                    join f in _context.crm_custom_fields
                        on v.field_id equals f.id
                    where contactIds.Contains(v.contact_id) && f.client_id == clientId
                    select new
                    {
                        v.contact_id,
                        f.field_name,
                        v.value
                    }
                ).ToListAsync();

                var customFieldsByContact = customValues
                    .GroupBy(x => x.contact_id)
                    .ToDictionary(
                        g => g.Key,
                        g => g
                            .Where(x => !string.IsNullOrWhiteSpace(x.field_name))
                            .GroupBy(x => x.field_name.Trim(), StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(
                                x => x.Key,
                                x => x.Last().value,
                                StringComparer.OrdinalIgnoreCase
                            )
                    );

                var contacts = contactsRaw
                    .Select(c => new
                    {
                        c.id,
                        c.full_name,
                        c.first_name,
                        c.last_name,
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
                        hasLinkedInInfo = !string.IsNullOrEmpty(c.linkedIninformation),
                        hasNotes = notesSet.Contains(c.id),
                        unsubscribe = !string.IsNullOrWhiteSpace(c.email) && unsubscribedSet.Contains(c.email) ? "Yes" : "No",
                        customFields = customFieldsByContact.TryGetValue(c.id, out var fields)
                            ? fields
                            : new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
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
        public async Task<IActionResult> GetLogs(
            [FromQuery] int clientId,
            [FromQuery] int? campaignId = null
        )
        {
            if (!campaignId.HasValue)
                return BadRequest("campaignId is required");

            var logs = await (
                from log in _context.EmailLogs
                join contact in _context.contacts
                on log.ContactId equals contact.id into contactGroup
                from contact in contactGroup.DefaultIfEmpty()
                where log.ClientId == clientId
                   && log.CampaignId == campaignId.Value   // ✅ ONLY FILTER
                orderby log.SentAt descending
                select new
                {
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
                    log.CampaignId,

                    Name = contact.full_name,
                    FirstName = contact.first_name,
                    LastName = contact.last_name,
                    Email = contact.email,
                    address = contact.country_or_address,
                    Website = contact.website,
                    Company = contact.company_name,
                    JobTitle = contact.job_title,
                    LinkedIn = contact.linkedin_url
                }
            ).ToListAsync();

            return Ok(logs);
        }
        [HttpGet("gettrackinglogs")]
        public async Task<IActionResult> GettrackingLogs(
            [FromQuery] int clientId,
            [FromQuery] int? campaignId = null
        )
        {
            if (!campaignId.HasValue)
                return BadRequest("campaignId is required");

            var logs = await (
                from t in _context.EmailTrackingLogs
                join e in _context.EmailLogs
                    on t.TrackingId equals e.TrackingId into emailGroup
                from e in emailGroup.DefaultIfEmpty()
                where t.ClientId == clientId
                   && t.CampaignId == campaignId.Value   // ✅ ONLY FILTER
                orderby t.Timestamp descending
                select new
                {
                    t.Id,
                    t.Email,
                    t.EventType,
                    t.Timestamp,
                    t.ClientId,
                    t.TargetUrl,
                    t.Full_Name,
                    t.Location,
                    t.Company,
                    t.JobTitle,
                    t.linkedin_URL,
                    t.website,
                    t.TrackingId,
                    t.IPAddress,
                    t.IsBot,
                    t.DataFileId,
                    t.ContactId,
                    t.SegmentId,
                    t.CampaignId,
                    SentAt = e != null ? e.SentAt : null
                }
            ).ToListAsync();

            return Ok(logs);
        }
        //[HttpGet("getlogs-by-segment")]
        //public async Task<IActionResult> GetLogsBySegment([FromQuery] int clientId, [FromQuery] int? segmentId= null, [FromQuery] int? campaignId = null)
        //{
        //    bool isValid = await _context.segments
        //        .AnyAsync(s => s.Id == segmentId && s.ClientId == clientId);

        //    if (!isValid)
        //        return BadRequest("Invalid clientId or segmentId.");

        //    var logs = await (
        //        from log in _context.EmailLogs
        //        join contact in _context.contacts
        //            on log.ContactId equals contact.id into contactGroup
        //        from contact in contactGroup.DefaultIfEmpty()
        //        where log.ClientId == clientId
        //              && log.SegmentId == segmentId   // 👈 Direct filter
        //              && (!campaignId.HasValue || log.CampaignId == campaignId.Value)
        //        orderby log.SentAt descending
        //        select new
        //        {
        //            log.Id,
        //            log.ContactId,
        //            log.ClientId,
        //            log.DataFileId,
        //            log.TrackingId,
        //            log.SegmentId,
        //            log.Subject,
        //            log.Body,
        //            log.SentAt,
        //            log.IsSuccess,
        //            log.ErrorMessage,
        //            log.ToEmail,
        //            log.process_name,
        //            log.CampaignId,

        //            Name = contact.full_name,
        //            Email = contact.email,
        //            Address = contact.country_or_address,
        //            Website = contact.website,
        //            Company = contact.company_name,
        //            JobTitle = contact.job_title,
        //            LinkedIn = contact.linkedin_url
        //        }
        //    )
        //    .Take(1000)
        //    .ToListAsync();

        //    return Ok(logs);
        //}

        //[HttpGet("gettrackinglogs-by-segment")]
        //public async Task<IActionResult> GetTrackingLogsBySegment([FromQuery] int clientId, [FromQuery] int? segmentId= null, [FromQuery] int? campaignId = null)
        //{
        //    // Step 1: Validate segmentId belongs to clientId
        //    bool isValid = await _context.segments
        //        .AnyAsync(s => s.Id == segmentId && s.ClientId == clientId);

        //    if (!isValid)
        //        return BadRequest("Invalid clientId or segmentId.");

        //    // Step 2: Use a join approach
        //    var logs = await (
        //        from t in _context.EmailTrackingLogs
        //        join e in _context.EmailLogs
        //            on new { t.TrackingId, t.ClientId, t.SegmentId }
        //            equals new { e.TrackingId, e.ClientId, e.SegmentId }
        //            into emailGroup
        //        from e in emailGroup.DefaultIfEmpty()   // 👈 left join
        //        where t.ClientId == clientId && t.SegmentId == segmentId && (!campaignId.HasValue || t.CampaignId == campaignId.Value)
        //        orderby t.Timestamp descending
        //        select new
        //        {
        //            t.Id,
        //            t.Email,
        //            t.EventType,
        //            t.Timestamp,
        //            t.ClientId,
        //            t.TargetUrl,
        //            t.ZohoViewName,
        //            t.Full_Name,
        //            t.Location,
        //            t.Company,
        //            t.JobTitle,
        //            t.linkedin_URL,
        //            t.website,
        //            t.TrackingId,
        //            t.UserAgent,
        //            t.IPAddress,
        //            t.IsBot,
        //            t.Browser,
        //            t.DataFileId,
        //            t.ContactId,
        //            t.SegmentId,
        //            t.CampaignId,
        //            t.BlueprintId,
        //            SentAt = e != null ? e.SentAt : null   // 👈 Second table se field
        //        }
        //    )
        //    .Take(1000)
        //    .ToListAsync();

        //    return Ok(logs);
        //}

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
                var contact = await _context.contacts.FirstOrDefaultAsync(c => c.id == contactId);
                if (contact == null)
                    return NotFound(new { message = "Contact not found." });

                var customValues = await _context.contact_custom_field_values
                    .Where(x => x.contact_id == contactId)
                    .ToListAsync();

                _context.contact_custom_field_values.RemoveRange(customValues);
                _context.contacts.Remove(contact);

                await _context.SaveChangesAsync();
                return Ok(new { message = "Contact deleted successfully." });
            }
            catch (DbUpdateException ex)
            {
                return BadRequest(new { message = ex.InnerException?.Message ?? ex.Message });
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
        public async Task<IActionResult> GetContactsBySegmentId(
     [FromQuery] int clientId,
     [FromQuery] int segmentId,
     [FromQuery] bool isFollowUp,
     [FromQuery] bool notKrafted,
     [FromQuery] bool kraftedNotSent)
        {
            if (clientId <= 0 || segmentId <= 0)
                return BadRequest("clientId aur segmentId dono 0 se bade hone chahiye.");

            var segment = await _context.segments
                .FirstOrDefaultAsync(s => s.Id == segmentId && s.ClientId == clientId);

            if (segment == null)
                return NotFound("Is client ke liye segment nahi mila.");

            // ✅ Base Query (JOIN with SegmentContacts)
            var query = _context.contacts
                .Where(c =>
                    _context.segmentContacts
                        .Any(sc => sc.SegmentId == segmentId && sc.ContactId == c.id)
                    &&
                    !_context.UnsubscribedContacts
                        .Any(uc => uc.ClientId == clientId && uc.Email == c.email)
                );

            // ✅ Filter: Not Krafted
            if (notKrafted)
            {
                query = query.Where(c => c.updated_at == null);
            }

            // ✅ Filter: Krafted but Not Sent
            if (kraftedNotSent)
            {
                query = query.Where(c => c.updated_at != null && c.email_sent_at == null);
            }

            var contacts = await query
                .OrderBy(c => c.id)
                .ToListAsync();

            var result = new List<object>();

            foreach (var c in contacts)
            {
                string finalEmailBody = c.email_body;

                if (isFollowUp)
                {
                    if (c.updated_at < c.email_sent_at)
                    {
                        finalEmailBody = "You have not krafted any email after sending the last email. Please kraft to continue.";
                    }

                    string oldThread = await _contactRepository
                        .BuildEmailThreadAsync(clientId, segment.DataFileId, c.id, segmentId);

                    finalEmailBody = $@"{finalEmailBody}

                    {oldThread}";
                }

                result.Add(new
                {
                    c.id,
                    c.full_name,
                    c.first_name,
                    c.last_name,
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
                // Load notes (only ContactIds)
                var notesContactIds = await _context.Notes
                    .Where(n => n.ClientId == clientId)
                    .Select(n => n.ContactId)
                    .Distinct()
                    .ToListAsync();

                var notesSet = new HashSet<int>(notesContactIds);
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
                        c.first_name,
                        c.last_name,
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
                        hasLinkedInInfo = !string.IsNullOrEmpty(c.linkedIninformation),

                        hasNotes = notesSet.Contains(c.id),
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
                        FirstName = c.first_name,
                        LastName = c.last_name,
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
                        c.first_name,
                        c.last_name,
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
                    c.first_name,
                    c.last_name,
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
                // 1️⃣ Fetch existing contact
                var existingContact = await _context.contacts
                    .FirstOrDefaultAsync(c => c.id == contactId);

                if (existingContact == null)
                    return NotFound(new
                    {
                        success = false,
                        message = "Contact not found"
                    });

                // 2️⃣ Clone contact (INCLUDING first_name + last_name ✅)
                var clonedContact = new Contact
                {
                    DataFileId = existingContact.DataFileId,

                    // ✅ NAME FIELDS (IMPORTANT)
                    first_name = existingContact.first_name,
                    last_name = existingContact.last_name,
                    full_name = existingContact.full_name,

                    // Other fields
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

        [HttpGet("contact-by-id")]
        public async Task<IActionResult> GetContactById([FromQuery] int contactId, [FromQuery] int clientId)
        {
            try
            {
                var contact = await _context.contacts
                    .Include(c => c.data_file)
                    .FirstOrDefaultAsync(c => c.id == contactId && c.data_file.client_id == clientId);

                if (contact == null)
                {
                    return NotFound(new { message = "Contact not found" });
                }

                // Custom fields
                var customFields = await (
                    from value in _context.contact_custom_field_values
                    join field in _context.crm_custom_fields
                        on value.field_id equals field.id
                    where value.contact_id == contactId
                    select new
                    {
                        field.field_name,
                        value.value
                    }
                )
                .GroupBy(x => x.field_name)
                .ToDictionaryAsync(
                    g => g.Key,
                    g => g.First().value
                );

                // Return SAFE object (no circular reference)
                var result = new
                {
                    contact.id,
                    contact.full_name,
                    contact.first_name,
                    contact.last_name,
                    contact.email,
                    contact.website,
                    contact.company_name,
                    contact.job_title,
                    contact.linkedin_url,
                    contact.country_or_address,
                    contact.email_subject,
                    contact.email_body,
                    contact.CompanyTelephone,
                    contact.CompanyEmployeeCount,
                    contact.CompanyIndustry,
                    contact.CompanyLinkedInURL,
                    contact.linkedIninformation,
                    contact.created_at,
                    contact.updated_at,
                    customFields
                };

                return Ok(result);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    message = "Error fetching contact",
                    error = ex.Message
                });
            }
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

        [HttpPost("updatetracking")]
        public async Task<IActionResult> UpdateCampaign([FromQuery] int clientId, [FromQuery] bool IsTracking)
        {
            try
            {
                var tracking = await _context.ClientDetails.FirstOrDefaultAsync(x => x.Id == clientId);
                // Update campaign properties
                tracking.IsTracking = IsTracking;
                // Update in database
                _context.ClientDetails.Update(tracking);
                await _context.SaveChangesAsync();

                return Ok("tracking updated successfully");
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = "An error occurred while updating the campaign", Error = ex.Message });
            }
        }

        [HttpGet("tracking-by-id")]
        public async Task<IActionResult> GetTrackingById([FromQuery] int clientId)
        {
            try
            {
                var tracking = await _context.ClientDetails
                    .Where(c => c.Id == clientId)
                    .Select(c => c.IsTracking)
                    .FirstOrDefaultAsync();

                if (tracking == null)
                {
                    return NotFound(new { message = "Client not found" });
                }

                return Ok(new
                {
                    clientId = clientId,
                    isTracking = tracking
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Error fetching tracking status" });
            }
        }
        [HttpGet("full-tracking-data")]
        public async Task<IActionResult> GetFullTrackingData([FromQuery] int clientId, [FromQuery] int dataFileId)
        {
            try
            {
                var result = await _contactRepository.GetFullTrackingData(clientId, dataFileId);

                if (result == null ||
                   (!result.Contacts.Any() && !result.EmailTrackingLogs.Any() && !result.EmailLogs.Any()))
                {
                    return NotFound(new { message = "No data found" });
                }

                return Ok(result);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    message = "Something went wrong",
                    error = ex.Message
                });
            }
        }


        [HttpPost("create-view")]
        public async Task<IActionResult> CreateView([FromBody] CreateViewDto dto)
        {
            using var transaction = await _context.Database.BeginTransactionAsync();

            try
            {
                var view = new CrmView
                {
                    client_id = dto.ClientId,
                    name = dto.Name,
                    description = dto.Description,
                    filters_json = dto.FiltersJson,
                    created_at = DateTime.UtcNow,
                    use_all_datafiles = dto.UseAllDataFiles
                };

                _context.crm_views.Add(view);
                await _context.SaveChangesAsync();

                if (!dto.UseAllDataFiles && dto.DataFileIds != null && dto.DataFileIds.Any())
                {
                    foreach (var df in dto.DataFileIds)
                    {
                        _context.crm_view_datafiles.Add(new CrmViewDatafile
                        {
                            view_id = view.id,
                            datafile_id = df
                        });
                    }
                }

                if (dto.UseAllDataFiles && dto.ExcludedDataFileIds != null && dto.ExcludedDataFileIds.Any())
                {
                    foreach (var df in dto.ExcludedDataFileIds)
                    {
                        _context.crm_view_excluded_datafiles.Add(new CrmViewExcludedDatafile
                        {
                            view_id = view.id,
                            datafile_id = df
                        });
                    }
                }

                if (dto.SegmentIds != null && dto.SegmentIds.Any())
                {
                    foreach (var seg in dto.SegmentIds)
                    {
                        _context.crm_view_segments.Add(new CrmViewSegment
                        {
                            view_id = view.id,
                            segment_id = seg
                        });
                    }
                }

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                return Ok(view);
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                return BadRequest(ex.Message);
            }
        }

        [HttpGet("views-by-client")]
        public async Task<IActionResult> GetViewsByClient(int clientId)
        {
            var views = await _context.crm_views
                .Where(v => v.client_id == clientId)
                .Select(v => new
                {
                    v.id,
                    v.name,
                    v.description,
                    v.created_at,
                    v.use_all_datafiles
                })
                .ToListAsync();

            return Ok(views);
        }

        [HttpPost("delete-view")]
        public async Task<IActionResult> DeleteView(int viewId)
        {
            using var transaction = await _context.Database.BeginTransactionAsync();

            try
            {
                var view = await _context.crm_views
                    .FirstOrDefaultAsync(v => v.id == viewId);

                if (view == null)
                    return NotFound();

                var datafiles = _context.crm_view_datafiles
                    .Where(x => x.view_id == viewId);

                var segments = _context.crm_view_segments
                    .Where(x => x.view_id == viewId);

                var excluded = _context.crm_view_excluded_datafiles
                    .Where(x => x.view_id == viewId);

                _context.crm_view_datafiles.RemoveRange(datafiles);
                _context.crm_view_segments.RemoveRange(segments);
                _context.crm_view_excluded_datafiles.RemoveRange(excluded);

                _context.crm_views.Remove(view);

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                return Ok("View deleted");
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                return BadRequest(ex.Message);
            }
        }

        [HttpPost("update-view")]
        public async Task<IActionResult> UpdateView([FromBody] UpdateViewDto dto)
        {
            using var transaction = await _context.Database.BeginTransactionAsync();

            try
            {
                var view = await _context.crm_views
                    .FirstOrDefaultAsync(v => v.id == dto.ViewId);

                if (view == null)
                    return NotFound("View not found");

                view.name = dto.Name;
                view.description = dto.Description;
                view.filters_json = dto.FiltersJson;
                view.use_all_datafiles = dto.UseAllDataFiles;

                _context.crm_views.Update(view);

                // Clear existing datafile links
                var existingDatafiles = _context.crm_view_datafiles
                    .Where(x => x.view_id == dto.ViewId);
                _context.crm_view_datafiles.RemoveRange(existingDatafiles);

                // Clear exclusions
                var existingExcluded = _context.crm_view_excluded_datafiles
                    .Where(x => x.view_id == dto.ViewId);
                _context.crm_view_excluded_datafiles.RemoveRange(existingExcluded);

                if (dto.UseAllDataFiles)
                {
                    // Store exclusions (unchecked)
                    if (dto.ExcludedDataFileIds != null && dto.ExcludedDataFileIds.Any())
                    {
                        var newExcluded = dto.ExcludedDataFileIds.Select(df => new CrmViewExcludedDatafile
                        {
                            view_id = dto.ViewId,
                            datafile_id = df
                        });

                        await _context.crm_view_excluded_datafiles.AddRangeAsync(newExcluded);
                    }
                }
                else
                {
                    if (dto.DataFileIds != null && dto.DataFileIds.Any())
                    {
                        var newDatafiles = dto.DataFileIds.Select(df => new CrmViewDatafile
                        {
                            view_id = dto.ViewId,
                            datafile_id = df
                        });

                        await _context.crm_view_datafiles.AddRangeAsync(newDatafiles);
                    }
                }

                // Update segments
                var existingSegments = _context.crm_view_segments
                    .Where(x => x.view_id == dto.ViewId);

                _context.crm_view_segments.RemoveRange(existingSegments);

                if (dto.SegmentIds != null && dto.SegmentIds.Any())
                {
                    var newSegments = dto.SegmentIds.Select(seg => new CrmViewSegment
                    {
                        view_id = dto.ViewId,
                        segment_id = seg
                    });

                    await _context.crm_view_segments.AddRangeAsync(newSegments);
                }

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                return Ok("View updated successfully");
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                return BadRequest(ex.Message);
            }
        }
        [HttpPost("view-contacts")]
        public async Task<IActionResult> GetViewContacts([FromBody] ViewContactsRequest dto)
        {
            try
            {
                var view = await _context.crm_views
                    .AsNoTracking()
                    .FirstOrDefaultAsync(v => v.id == dto.ViewId && v.client_id == dto.ClientId);

                if (view == null)
                    return NotFound(new { success = false, message = "View not found" });

                FiltersPayload? payload = null;

                if (!string.IsNullOrWhiteSpace(view.filters_json))
                {
                    try
                    {
                        payload = JsonSerializer.Deserialize<FiltersPayload>(
                            view.filters_json,
                            new JsonSerializerOptions
                            {
                                PropertyNameCaseInsensitive = true
                            });
                    }
                    catch (Exception parseEx)
                    {
                        return BadRequest(new
                        {
                            success = false,
                            message = "Invalid filters_json saved for this view",
                            error = parseEx.Message
                        });
                    }
                }

                var validationError = TrackingFilterHelper.ValidateTrackingFilters(payload);
                if (!string.IsNullOrWhiteSpace(validationError))
                {
                    return BadRequest(new
                    {
                        success = false,
                        message = validationError
                    });
                }

                List<int> dataFileIds;

                if (view.use_all_datafiles)
                {
                    var all = await _context.data_files
                        .AsNoTracking()
                        .Where(df => df.client_id == dto.ClientId)
                        .Select(df => df.id)
                        .ToListAsync();

                    var excluded = await _context.crm_view_excluded_datafiles
                        .AsNoTracking()
                        .Where(x => x.view_id == view.id)
                        .Select(x => x.datafile_id)
                        .ToListAsync();

                    dataFileIds = all.Except(excluded).Distinct().ToList();
                }
                else
                {
                    dataFileIds = await _context.crm_view_datafiles
                        .AsNoTracking()
                        .Where(x => x.view_id == view.id)
                        .Select(x => x.datafile_id)
                        .Distinct()
                        .ToListAsync();
                }

                if (!dataFileIds.Any())
                {
                    return Ok(new
                    {
                        total = 0,
                        page = dto.Page <= 0 ? 1 : dto.Page,
                        pageSize = dto.PageSize <= 0 ? 0 : dto.PageSize,
                        contacts = new List<object>()
                    });
                }

                var query = _context.contacts
                    .AsNoTracking()
                    .Where(c =>
                        c.DataFileId.HasValue &&
                        dataFileIds.Contains(c.DataFileId.Value) &&
                        !_context.UnsubscribedContacts
                            .Any(uc => uc.ClientId == dto.ClientId && uc.Email == c.email));

                var segmentIds = await _context.crm_view_segments
                    .AsNoTracking()
                    .Where(x => x.view_id == view.id)
                    .Select(x => x.segment_id)
                    .ToListAsync();

                if (segmentIds.Any())
                {
                    var segmentContactIds = _context.segmentContacts
                        .AsNoTracking()
                        .Where(x => segmentIds.Contains(x.SegmentId))
                        .Select(x => x.ContactId);

                    query = query.Where(c => segmentContactIds.Contains(c.id));
                }

                if (dto.NotKrafted)
                {
                    query = query.Where(c => c.updated_at == null);
                }

                if (dto.KraftedNotSent)
                {
                    query = query.Where(c => c.updated_at != null && c.email_sent_at == null);
                }

                if (!string.IsNullOrWhiteSpace(dto.Search))
                {
                    var s = dto.Search.Trim();

                    query = query.Where(c =>
                        EF.Functions.Like(c.full_name ?? "", $"%{s}%") ||
                        EF.Functions.Like(c.email ?? "", $"%{s}%") ||
                        EF.Functions.Like(c.company_name ?? "", $"%{s}%") ||
                        EF.Functions.Like(c.job_title ?? "", $"%{s}%"));
                }

                var contacts = await query
                    .OrderBy(c => c.id)
                    .Select(c => new
                    {
                        c.id,
                        c.DataFileId,
                        c.full_name,
                        c.first_name,
                        c.last_name,
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
                        c.linkedIninformation
                    })
                    .ToListAsync();

                if (!contacts.Any())
                {
                    return Ok(new
                    {
                        total = 0,
                        page = dto.Page <= 0 ? 1 : dto.Page,
                        pageSize = dto.PageSize <= 0 ? 0 : dto.PageSize,
                        contacts = new List<object>()
                    });
                }

                var contactIds = contacts.Select(c => c.id).ToList();

                var contactEmails = contacts
                    .Select(c => string.IsNullOrWhiteSpace(c.email) ? "" : c.email.Trim().ToLower())
                    .Where(x => x.Length > 0)
                    .Distinct()
                    .ToList();

                var notesSet = new HashSet<int>(
                    await _context.Notes
                        .AsNoTracking()
                        .Where(n => n.ClientId == dto.ClientId && contactIds.Contains(n.ContactId))
                        .Select(n => n.ContactId)
                        .Distinct()
                        .ToListAsync()
                );

                var customValues = await (
                    from v in _context.contact_custom_field_values.AsNoTracking()
                    join f in _context.crm_custom_fields.AsNoTracking()
                        on v.field_id equals f.id
                    where contactIds.Contains(v.contact_id) && f.client_id == dto.ClientId
                    select new
                    {
                        v.contact_id,
                        f.field_name,
                        v.value
                    }
                ).ToListAsync();

                var customByContact = new Dictionary<int, Dictionary<string, object?>>();

                foreach (var row in customValues)
                {
                    if (string.IsNullOrWhiteSpace(row.field_name))
                        continue;

                    var fieldName = row.field_name.Trim();

                    if (!customByContact.TryGetValue(row.contact_id, out var fields))
                    {
                        fields = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                        customByContact[row.contact_id] = fields;
                    }

                    fields[fieldName] = row.value;
                }

                var emailThreadByContactId = new Dictionary<int, string>();

                if (dto.IsFollowUp)
                {
                    var logs = await _context.EmailLogs
                        .AsNoTracking()
                        .Where(x =>
                            x.ClientId == dto.ClientId &&
                            x.ContactId.HasValue &&
                            contactIds.Contains(x.ContactId.Value) &&
                            x.IsSuccess == true)
                        .OrderByDescending(x => x.SentAt)
                        .ToListAsync();

                    emailThreadByContactId = logs
                        .GroupBy(x => x.ContactId!.Value)
                        .ToDictionary(
                            g => g.Key,
                            g =>
                            {
                                var sb = new StringBuilder();

                                foreach (var log in g)
                                {
                                    sb.AppendLine("<hr style='border:0; border-top:0.5px solid #999; width:100%;' />");
                                    sb.AppendLine($"<b>From:</b> {log.EmailSenderName} &lt;{log.SenderEmailId}&gt;<br/>");
                                    sb.AppendLine($"<b>Sent:</b> {log.SentAt:dddd, MMMM d, yyyy h:mm tt}<br/>");
                                    sb.AppendLine($"<b>To:</b> {log.EmailRecipientName} &lt;{log.ToEmail}&gt;<br/>");
                                    sb.AppendLine($"<b>Subject:</b> {log.Subject}<br/><br/>");
                                    sb.AppendLine($"{log.Body}<br/><br/>");
                                }

                                return sb.ToString();
                            });
                }

                var result = new List<object>();

                foreach (var c in contacts)
                {
                    var customFields = customByContact.TryGetValue(c.id, out var found)
                        ? found
                        : new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

                    string finalEmailBody = c.email_body;

                    if (dto.IsFollowUp)
                    {
                        if (c.updated_at < c.email_sent_at)
                        {
                            finalEmailBody = "You have not krafted any email after sending the last email. Please kraft to continue.";
                        }

                        emailThreadByContactId.TryGetValue(c.id, out var oldThread);

                        finalEmailBody = $@"{finalEmailBody}

                    {oldThread}";
                    }

                    result.Add(new
                    {
                        c.id,
                        c.DataFileId,
                        c.full_name,
                        c.first_name,
                        c.last_name,
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
                        linkedIninformation = c.linkedIninformation,
                        hasNotes = notesSet.Contains(c.id),
                        hasLinkedInInfo = !string.IsNullOrWhiteSpace(c.linkedIninformation),
                        customFields = customFields
                    });
                }

                var trackingContext = await TrackingFilterHelper.BuildTrackingFilterContextAsync(
                    _context,
                    dto.ClientId,
                    payload,
                    contactIds,
                    contactEmails
                );

                var filtered = TrackingFilterHelper.ApplyFilters(result, payload, trackingContext);

                var safePage = dto.Page <= 0 ? 1 : dto.Page;
                var safePageSize = dto.PageSize <= 0 ? filtered.Count : dto.PageSize;

                var pagedContacts = filtered
                    .Skip((safePage - 1) * safePageSize)
                    .Take(safePageSize)
                    .ToList();

                return Ok(new
                {
                    total = filtered.Count,
                    page = safePage,
                    pageSize = safePageSize,
                    contacts = pagedContacts
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


        [HttpGet("get-contact-columns")]
        public async Task<IActionResult> GetContactColumns(int clientId)
        {
            var result = await _contactRepository.GetContactColumnsWithCustomFields(clientId);

            return Ok(new
            {
                success = true,
                data = result
            });
        }

        [HttpPost("upload-datafile")]
        public async Task<IActionResult> UploadDataFile([FromBody] DataFileDto request)
        {
            try
            {
                if (request.clientId <= 0)
                {
                    return BadRequest(new
                    {
                        success = false,
                        message = "clientId is required"
                    });
                }

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

                return Ok(new
                {
                    success = true,
                    message = "DataFile created successfully",
                    dataFileId = dataFile.id
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new
                {
                    success = false,
                    message = "DataFile creation failed",
                    error = ex.InnerException?.Message ?? ex.Message
                });
            }
        }

        [HttpPost("bulk-update-field")]
        public async Task<IActionResult> BulkUpdateField([FromBody] BulkUpdateFieldDto dto)
        {
            var result = await _contactRepository.BulkUpdateFieldAsync(dto);

            return Ok(new
            {
                success = result,
                message = "Field updated successfully"
            });
        }






    }

}
