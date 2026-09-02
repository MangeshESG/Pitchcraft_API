//using Microsoft.AspNetCore.Mvc;
//using Microsoft.EntityFrameworkCore;
//using Newtonsoft.Json;
//using PitchGenApi.Database;
//using PitchGenApi.Interfaces;
//using PitchGenApi.Model;
//using PitchGenApi.Model.DTOs;
//using PitchGenApi.Services;

//namespace PitchGenApi.Controllers
//{
//    /// <summary>
//    /// Audience Assurance: the saved targeting briefs, the validation runs and
//    /// their results.
//    ///
//    /// Runs are queued rather than executed here. A hundred contacts with web
//    /// search enabled takes minutes, so the caller gets a job id back at once
//    /// and polls <c>job/{id}</c> while the background worker does the work.
//    /// </summary>
//    [ApiController]
//    [Route("api/[controller]")]
//    public class ContactValidationController : ControllerBase
//    {
//        private readonly AppDbContext _context;
//        private readonly IContactValidationService _validationService;

//        public ContactValidationController(
//            AppDbContext context,
//            IContactValidationService validationService)
//        {
//            _context = context;
//            _validationService = validationService;
//        }

//        // =============================================================
//        // Briefs
//        // =============================================================

//        [HttpGet("briefs")]
//        public async Task<IActionResult> GetBriefs([FromQuery] int clientId)
//        {
//            if (clientId <= 0)
//                return BadRequest(new { success = false, message = "clientId must be greater than 0." });

//            var briefs = await _context.contact_fit_briefs
//                .AsNoTracking()
//                .Where(b => b.ClientId == clientId)
//                // Default first, then alphabetical: the run panel preselects the
//                // default and the list reads as "the usual one, then the rest".
//                .OrderByDescending(b => b.IsDefault)
//                .ThenBy(b => b.Name)
//                .Select(b => new ContactFitBriefDto
//                {
//                    Id = b.Id,
//                    Name = b.Name,
//                    BriefText = b.BriefText,
//                    IsDefault = b.IsDefault,
//                    CreatedAt = b.CreatedAt,
//                    UpdatedAt = b.UpdatedAt,
//                    UpdatedBy = b.UpdatedBy
//                })
//                .ToListAsync();

//            return Ok(new { success = true, briefs });
//        }

//        [HttpPost("briefs")]
//        public async Task<IActionResult> SaveBrief([FromBody] SaveContactFitBriefDto dto)
//        {
//            if (dto == null || dto.ClientId <= 0)
//                return BadRequest(new { success = false, message = "A valid client is required." });

//            if (string.IsNullOrWhiteSpace(dto.Name))
//                return BadRequest(new { success = false, message = "Give the brief a name." });

//            if (string.IsNullOrWhiteSpace(dto.BriefText))
//                return BadRequest(new { success = false, message = "The brief cannot be empty." });

//            var name = dto.Name.Trim();

//            var clash = await _context.contact_fit_briefs
//                .AnyAsync(b => b.ClientId == dto.ClientId && b.Name == name && b.Id != dto.Id);

//            if (clash)
//                return BadRequest(new { success = false, message = $"A brief called '{name}' already exists." });

//            ContactFitBrief brief;

//            if (dto.Id > 0)
//            {
//                var existing = await _context.contact_fit_briefs
//                    .FirstOrDefaultAsync(b => b.Id == dto.Id && b.ClientId == dto.ClientId);

//                if (existing == null)
//                    return NotFound(new { success = false, message = "That brief no longer exists." });

//                brief = existing;
//                brief.Name = name;
//                brief.BriefText = dto.BriefText;
//                brief.UpdatedAt = DateTime.UtcNow;
//                brief.UpdatedBy = dto.UpdatedBy;
//            }
//            else
//            {
//                brief = new ContactFitBrief
//                {
//                    ClientId = dto.ClientId,
//                    Name = name,
//                    BriefText = dto.BriefText,
//                    CreatedAt = DateTime.UtcNow,
//                    UpdatedBy = dto.UpdatedBy
//                };

//                _context.contact_fit_briefs.Add(brief);
//            }

//            if (dto.IsDefault)
//                await ClearOtherDefaultsAsync(dto.ClientId, brief);

//            brief.IsDefault = dto.IsDefault;

//            await _context.SaveChangesAsync();

//            return Ok(new { success = true, brief = ToDto(brief) });
//        }

//        [HttpPost("briefs/set-default")]
//        public async Task<IActionResult> SetDefaultBrief([FromQuery] int clientId, [FromQuery] int briefId)
//        {
//            var brief = await _context.contact_fit_briefs
//                .FirstOrDefaultAsync(b => b.Id == briefId && b.ClientId == clientId);

//            if (brief == null)
//                return NotFound(new { success = false, message = "That brief no longer exists." });

//            await ClearOtherDefaultsAsync(clientId, brief);
//            brief.IsDefault = true;
//            brief.UpdatedAt = DateTime.UtcNow;

//            await _context.SaveChangesAsync();

//            return Ok(new { success = true, brief = ToDto(brief) });
//        }

//        [HttpPost("briefs/delete/{id}")]
//        public async Task<IActionResult> DeleteBrief(int id, [FromQuery] int clientId)
//        {
//            var brief = await _context.contact_fit_briefs
//                .FirstOrDefaultAsync(b => b.Id == id && b.ClientId == clientId);

//            if (brief == null)
//                return NotFound(new { success = false, message = "That brief no longer exists." });

//            _context.contact_fit_briefs.Remove(brief);
//            await _context.SaveChangesAsync();

//            // Scores stay: contact_validations.contact_fit_brief_id is kept as a
//            // record of what was scored against, and deleting the brief must not
//            // silently erase results the user is still looking at.
//            return Ok(new { success = true, message = "Brief deleted. Existing scores were kept." });
//        }

//        /// <summary>
//        /// Clears the flag on whatever else currently holds it, so the filtered
//        /// unique index cannot be violated by promoting a second brief.
//        /// </summary>
//        private async Task ClearOtherDefaultsAsync(int clientId, ContactFitBrief keeping)
//        {
//            var others = await _context.contact_fit_briefs
//                .Where(b => b.ClientId == clientId && b.IsDefault)
//                .ToListAsync();

//            foreach (var other in others.Where(o => !ReferenceEquals(o, keeping)))
//                other.IsDefault = false;
//        }

//        private static ContactFitBriefDto ToDto(ContactFitBrief brief) => new()
//        {
//            Id = brief.Id,
//            Name = brief.Name,
//            BriefText = brief.BriefText,
//            IsDefault = brief.IsDefault,
//            CreatedAt = brief.CreatedAt,
//            UpdatedAt = brief.UpdatedAt,
//            UpdatedBy = brief.UpdatedBy
//        };

//        // =============================================================
//        // Runs
//        // =============================================================

//        [HttpPost("run")]
//        public async Task<IActionResult> Run([FromBody] RunValidationRequestDto request)
//        {
//            try
//            {
//                var job = await _validationService.QueueAsync(request);
//                return Ok(new { success = true, job });
//            }
//            catch (InvalidOperationException ex)
//            {
//                // QueueAsync raises these with a message written for the user —
//                // no brief chosen, prompt not configured, not enough credit.
//                return BadRequest(new { success = false, message = ex.Message });
//            }
//        }

//        [HttpGet("job/{id}")]
//        public async Task<IActionResult> GetJob(int id, [FromQuery] int clientId)
//        {
//            var job = await _context.contact_validation_jobs
//                .AsNoTracking()
//                .FirstOrDefaultAsync(j => j.Id == id && j.ClientId == clientId);

//            if (job == null)
//                return NotFound(new { success = false, message = "No such validation run." });

//            return Ok(new { success = true, job = ContactValidationService.ToDto(job) });
//        }

//        /// <summary>
//        /// The cost log: what each run consumed and what it cost. This is the
//        /// table the searches-per-100 figure comes out of, which is what credit
//        /// pricing has to be set from.
//        /// </summary>
//        [HttpGet("jobs")]
//        public async Task<IActionResult> GetJobs([FromQuery] int clientId, [FromQuery] int take = 50)
//        {
//            if (clientId <= 0)
//                return BadRequest(new { success = false, message = "clientId must be greater than 0." });

//            var jobs = await _context.contact_validation_jobs
//                .AsNoTracking()
//                .Where(j => j.ClientId == clientId)
//                .OrderByDescending(j => j.CreatedAt)
//                .Take(Math.Clamp(take, 1, 200))
//                .ToListAsync();

//            return Ok(new
//            {
//                success = true,
//                jobs = jobs.Select(ContactValidationService.ToDto)
//            });
//        }

//        // =============================================================
//        // Results
//        // =============================================================

//        [HttpGet("results")]
//        public async Task<IActionResult> GetResults(
//            [FromQuery] int clientId,
//            [FromQuery] string? contactIds = null)
//        {
//            if (clientId <= 0)
//                return BadRequest(new { success = false, message = "clientId must be greater than 0." });

//            var query = _context.contact_validations
//                .AsNoTracking()
//                .Where(v => v.ClientId == clientId);

//            if (!string.IsNullOrWhiteSpace(contactIds))
//            {
//                var ids = contactIds
//                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
//                    .Select(value => int.TryParse(value, out var id) ? id : 0)
//                    .Where(id => id > 0)
//                    .ToList();

//                if (ids.Count == 0)
//                    return Ok(new { success = true, results = Array.Empty<ContactValidationDto>() });

//                query = query.Where(v => ids.Contains(v.ContactId));
//            }

//            var rows = await query.ToListAsync();

//            return Ok(new { success = true, results = rows.Select(ToDto) });
//        }

//        [HttpPost("mark-verified")]
//        public async Task<IActionResult> MarkVerified([FromBody] MarkVerifiedRequestDto request)
//        {
//            if (request == null || request.ClientId <= 0)
//                return BadRequest(new { success = false, message = "A valid client is required." });

//            var ids = (request.ContactIds ?? new List<int>()).Where(id => id > 0).Distinct().ToList();

//            if (ids.Count == 0)
//                return BadRequest(new { success = false, message = "Select at least one contact." });

//            var existing = await _context.contact_validations
//                .Where(v => v.ClientId == request.ClientId && ids.Contains(v.ContactId))
//                .ToListAsync();

//            var byContact = existing.ToDictionary(v => v.ContactId);
//            var now = DateTime.UtcNow;

//            foreach (var contactId in ids)
//            {
//                if (!byContact.TryGetValue(contactId, out var row))
//                {
//                    // A contact can be marked verified before any check has run
//                    // — the user has confirmed it themselves — so the row is
//                    // created with the scores left null.
//                    row = new ContactValidation
//                    {
//                        ClientId = request.ClientId,
//                        ContactId = contactId,
//                        CreatedAt = now
//                    };

//                    _context.contact_validations.Add(row);
//                }

//                row.IsVerified = request.IsVerified;
//                row.VerifiedAt = request.IsVerified ? now : null;
//                row.VerifiedBy = request.IsVerified ? request.VerifiedBy : null;
//                row.UpdatedAt = now;
//            }

//            await _context.SaveChangesAsync();

//            return Ok(new
//            {
//                success = true,
//                message = request.IsVerified
//                    ? $"{ids.Count} contact(s) marked as verified."
//                    : $"The verified mark was removed from {ids.Count} contact(s)."
//            });
//        }

//        private static ContactValidationDto ToDto(ContactValidation row)
//        {
//            var sources = new List<ValidationSourceDto>();

//            if (!string.IsNullOrWhiteSpace(row.SourcesJson))
//            {
//                try
//                {
//                    sources = JsonConvert.DeserializeObject<List<ValidationSourceDto>>(row.SourcesJson)
//                              ?? new List<ValidationSourceDto>();
//                }
//                catch (JsonException)
//                {
//                    // A malformed sources blob must not take the whole row's
//                    // scores down with it.
//                    sources = new List<ValidationSourceDto>();
//                }
//            }

//            return new ContactValidationDto
//            {
//                ContactId = row.ContactId,
//                ContactFitConfidence = row.ContactFitConfidence,
//                ContactFitComments = row.ContactFitComments,
//                ContactFitBriefId = row.ContactFitBriefId,
//                ContactFitCheckedAt = row.ContactFitCheckedAt,
//                DataIntegrityConfidence = row.DataIntegrityConfidence,
//                DataIntegrityComments = row.DataIntegrityComments,
//                DataIntegrityCheckedAt = row.DataIntegrityCheckedAt,
//                LiveContactConfidence = row.LiveContactConfidence,
//                LiveContactComments = row.LiveContactComments,
//                LiveContactCheckedAt = row.LiveContactCheckedAt,
//                EmailValidityConfidence = row.EmailValidityConfidence,
//                EmailValidityStatus = row.EmailValidityStatus,
//                EmailValiditySource = row.EmailValiditySource,
//                EmailValidityComments = row.EmailValidityComments,
//                EmailCheckedAt = row.EmailCheckedAt,
//                Sources = sources,
//                IsVerified = row.IsVerified,
//                VerifiedAt = row.VerifiedAt,
//                VerifiedBy = row.VerifiedBy
//            };
//        }

//        // =============================================================
//        // Metadata for the run panel
//        // =============================================================

//        /// <summary>
//        /// The four checks with their labels, so the run panel does not have to
//        /// keep its own copy of the descriptions.
//        /// </summary>
//        [HttpGet("check-types")]
//        public IActionResult GetCheckTypes() => Ok(new
//        {
//            success = true,
//            checkTypes = ValidationCheckTypes.All.Select(key =>
//            {
//                var (label, description) = ValidationCheckTypes.Describe(key);

//                return new
//                {
//                    key,
//                    label,
//                    description,
//                    requiresBrief = ValidationCheckTypes.RequiresBrief(key),
//                    usesWebSearch = ValidationCheckTypes.UsesWebSearch(key)
//                };
//            })
//        });
//    }
//}
