using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using PitchGenApi.Background;
using PitchGenApi.Database;
using PitchGenApi.Interfaces;
using PitchGenApi.Model;
using PitchGenApi.Model.DTOs;
using PitchGenApi.Services;

namespace PitchGenApi.Controllers
{
    /// <summary>
    /// Audience Assurance: the saved targeting briefs, the validation runs and
    /// their results.
    ///
    /// Runs are queued rather than executed here. A hundred contacts with web
    /// search enabled takes minutes, so the caller gets a job id back at once
    /// and polls <c>job/{id}</c> while the background worker does the work.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public class ContactValidationController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly IContactValidationService _validationService;
        private readonly IValidationSettingsService _validationSettings;
        private readonly ValidationRunnerDiagnostics _runnerDiagnostics;
        private readonly IServiceScopeFactory _scopeFactory;

        public ContactValidationController(
            AppDbContext context,
            IContactValidationService validationService,
            IValidationSettingsService validationSettings,
            ValidationRunnerDiagnostics runnerDiagnostics,
            IServiceScopeFactory scopeFactory)
        {
            _context = context;
            _validationService = validationService;
            _validationSettings = validationSettings;
            _runnerDiagnostics = runnerDiagnostics;
            _scopeFactory = scopeFactory;
        }

        // =============================================================
        // Admin tuning
        // =============================================================

        /// <summary>
        /// The tuning values behind a run, for the admin page.
        ///
        /// Readable without an admin check: the numbers are not sensitive, and
        /// the page that shows them is already admin-only. Changing one is
        /// what requires proving it.
        /// </summary>
        [HttpGet("settings")]
        public async Task<IActionResult> GetSettings()
        {
            var metadata = await _validationSettings.GetBatchSizeMetadataAsync();

            return Ok(new
            {
                success = true,
                batchSize = await _validationSettings.GetBatchSizeAsync(),
                defaultBatchSize = ValidationSettingKeys.DefaultBatchSize,
                minBatchSize = ValidationSettingKeys.MinBatchSize,
                maxBatchSize = ValidationSettingKeys.MaxBatchSize,
                updatedAt = metadata?.UpdatedAt,
                updatedBy = metadata?.UpdatedBy
            });
        }

        /// <summary>
        /// Sets how many contacts go into one model request. Applies to the
        /// next run — a run already going keeps the size it started with.
        /// </summary>
        [HttpPost("settings/batch-size")]
        public async Task<IActionResult> UpdateBatchSize(
            [FromBody] UpdateValidationBatchSizeRequest request)
        {
            if (request == null)
                return BadRequest(new { success = false, message = "Request body is required." });

            if (request.BatchSize < ValidationSettingKeys.MinBatchSize ||
                request.BatchSize > ValidationSettingKeys.MaxBatchSize)
            {
                return BadRequest(new
                {
                    success = false,
                    message =
                        $"The batch size must be between {ValidationSettingKeys.MinBatchSize} " +
                        $"and {ValidationSettingKeys.MaxBatchSize}."
                });
            }

            // This changes what every client's runs cost, so the caller has to
            // be an admin — the UI hiding the page is not enough on its own.
            var isAdmin = await _context.ClientDetails
                .AsNoTracking()
                .Where(client => client.Id == request.UpdatedBy)
                .Select(client => (bool?)client.IsAdmin)
                .FirstOrDefaultAsync();

            if (isAdmin != true)
            {
                return StatusCode(403, new
                {
                    success = false,
                    message = "Only an admin can change validation settings."
                });
            }

            var saved = await _validationSettings.SetBatchSizeAsync(
                request.BatchSize,
                request.UpdatedBy.ToString());

            return Ok(new
            {
                success = true,
                batchSize = saved,
                message = $"Runs will now send {saved} contact{(saved == 1 ? "" : "s")} per request."
            });
        }

        public class UpdateValidationBatchSizeRequest
        {
            public int BatchSize { get; set; }

            /// <summary>Client id of the admin making the change.</summary>
            public int UpdatedBy { get; set; }
        }

        // =============================================================
        // Runner health
        // =============================================================

        /// <summary>
        /// What the background runner is doing right now, readable over HTTP.
        ///
        /// The console output of the deployed process is not reachable, so
        /// when runs pile up at "queued" there is otherwise no way to tell
        /// whether the loop never started, crashed, or is running and failing
        /// every cycle. <c>isAlive</c> is the field that matters: the loop
        /// polls every three seconds, so anything but a single-digit
        /// <c>secondsSinceLastPoll</c> means it is not running.
        /// </summary>
        [HttpGet("runner/status")]
        public IActionResult RunnerStatus()
        {
            return Ok(new { success = true, runner = _runnerDiagnostics.Snapshot() });
        }

        /// <summary>
        /// Claims and runs queued jobs on demand, without waiting for the
        /// background loop.
        ///
        /// An escape hatch, not the normal path: if the hosted service is not
        /// running on a given deployment, this is what gets a stuck queue
        /// moving without a redeploy or a database edit. It uses the same
        /// atomic claim as the runner, so calling it while the runner is alive
        /// is safe — the two cannot pick up the same job.
        /// </summary>
        [HttpPost("runner/drain")]
        public async Task<IActionResult> DrainQueue([FromQuery] int maxJobs = 3)
        {
            if (maxJobs is < 1 or > 25)
                return BadRequest(new { success = false, message = "maxJobs must be between 1 and 25." });

            var owner = $"manual-drain:{Environment.MachineName}";

            var claimed = await _validationService.ClaimQueuedJobsAsync(maxJobs, owner);

            if (claimed.Count == 0)
                return Ok(new { success = true, claimed = 0, message = "Nothing was queued." });

            // Deliberately not awaited: these runs take minutes and the request
            // must not be held open for them. Each gets its own scope because
            // this request's scope is disposed as soon as the response is sent.
            foreach (var jobId in claimed)
            {
                var id = jobId;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var scope = _scopeFactory.CreateScope();

                        var service = scope.ServiceProvider
                            .GetRequiredService<IContactValidationService>();

                        await service.ProcessJobAsync(id, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"❌ Manual drain of job {id} failed: {ex}");
                    }
                });
            }

            return Ok(new
            {
                success = true,
                claimed = claimed.Count,
                jobIds = claimed,
                message = "Claimed and started. Poll job/{id} for progress."
            });
        }

        // =============================================================
        // Briefs
        // =============================================================

        [HttpGet("briefs")]
        public async Task<IActionResult> GetBriefs([FromQuery] int clientId)
        {
            if (clientId <= 0)
                return BadRequest(new { success = false, message = "clientId must be greater than 0." });

            var briefs = await _context.contact_fit_briefs
                .AsNoTracking()
                .Where(b => b.ClientId == clientId)
                // Default first, then alphabetical: the run panel preselects the
                // default and the list reads as "the usual one, then the rest".
                .OrderByDescending(b => b.IsDefault)
                .ThenBy(b => b.Name)
                .Select(b => new ContactFitBriefDto
                {
                    Id = b.Id,
                    Name = b.Name,
                    BriefText = b.BriefText,
                    IsDefault = b.IsDefault,
                    CreatedAt = b.CreatedAt,
                    UpdatedAt = b.UpdatedAt,
                    UpdatedBy = b.UpdatedBy
                })
                .ToListAsync();

            return Ok(new { success = true, briefs });
        }

        [HttpPost("briefs")]
        public async Task<IActionResult> SaveBrief([FromBody] SaveContactFitBriefDto dto)
        {
            if (dto == null || dto.ClientId <= 0)
                return BadRequest(new { success = false, message = "A valid client is required." });

            if (string.IsNullOrWhiteSpace(dto.Name))
                return BadRequest(new { success = false, message = "Give the brief a name." });

            if (string.IsNullOrWhiteSpace(dto.BriefText))
                return BadRequest(new { success = false, message = "The brief cannot be empty." });

            var name = dto.Name.Trim();

            var clash = await _context.contact_fit_briefs
                .AnyAsync(b => b.ClientId == dto.ClientId && b.Name == name && b.Id != dto.Id);

            if (clash)
                return BadRequest(new { success = false, message = $"A brief called '{name}' already exists." });

            ContactFitBrief brief;

            if (dto.Id > 0)
            {
                var existing = await _context.contact_fit_briefs
                    .FirstOrDefaultAsync(b => b.Id == dto.Id && b.ClientId == dto.ClientId);

                if (existing == null)
                    return NotFound(new { success = false, message = "That brief no longer exists." });

                brief = existing;
                brief.Name = name;
                brief.BriefText = dto.BriefText;
                brief.UpdatedAt = DateTime.UtcNow;
                brief.UpdatedBy = dto.UpdatedBy;
            }
            else
            {
                brief = new ContactFitBrief
                {
                    ClientId = dto.ClientId,
                    Name = name,
                    BriefText = dto.BriefText,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedBy = dto.UpdatedBy
                };

                _context.contact_fit_briefs.Add(brief);
            }

            if (dto.IsDefault)
                await ClearOtherDefaultsAsync(dto.ClientId, brief);

            brief.IsDefault = dto.IsDefault;

            await _context.SaveChangesAsync();

            return Ok(new { success = true, brief = ToDto(brief) });
        }

        [HttpPost("briefs/set-default")]
        public async Task<IActionResult> SetDefaultBrief([FromQuery] int clientId, [FromQuery] int briefId)
        {
            var brief = await _context.contact_fit_briefs
                .FirstOrDefaultAsync(b => b.Id == briefId && b.ClientId == clientId);

            if (brief == null)
                return NotFound(new { success = false, message = "That brief no longer exists." });

            await ClearOtherDefaultsAsync(clientId, brief);
            brief.IsDefault = true;
            brief.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            return Ok(new { success = true, brief = ToDto(brief) });
        }

        [HttpPost("briefs/delete/{id}")]
        public async Task<IActionResult> DeleteBrief(int id, [FromQuery] int clientId)
        {
            var brief = await _context.contact_fit_briefs
                .FirstOrDefaultAsync(b => b.Id == id && b.ClientId == clientId);

            if (brief == null)
                return NotFound(new { success = false, message = "That brief no longer exists." });

            _context.contact_fit_briefs.Remove(brief);
            await _context.SaveChangesAsync();

            // Scores stay: contact_validations.contact_fit_brief_id is kept as a
            // record of what was scored against, and deleting the brief must not
            // silently erase results the user is still looking at.
            return Ok(new { success = true, message = "Brief deleted. Existing scores were kept." });
        }

        /// <summary>
        /// Clears the flag on whatever else currently holds it, so the filtered
        /// unique index cannot be violated by promoting a second brief.
        /// </summary>
        private async Task ClearOtherDefaultsAsync(int clientId, ContactFitBrief keeping)
        {
            var others = await _context.contact_fit_briefs
                .Where(b => b.ClientId == clientId && b.IsDefault)
                .ToListAsync();

            foreach (var other in others.Where(o => !ReferenceEquals(o, keeping)))
                other.IsDefault = false;
        }

        private static ContactFitBriefDto ToDto(ContactFitBrief brief) => new()
        {
            Id = brief.Id,
            Name = brief.Name,
            BriefText = brief.BriefText,
            IsDefault = brief.IsDefault,
            CreatedAt = brief.CreatedAt,
            UpdatedAt = brief.UpdatedAt,
            UpdatedBy = brief.UpdatedBy
        };

         //=============================================================
         //Runs
         //=============================================================

        [HttpPost("run")]
        public async Task<IActionResult> Run([FromBody] RunValidationRequestDto request)
        {
            try
            {
                var job = await _validationService.QueueAsync(request);
                return Ok(new { success = true, job });
            }
            catch (InvalidOperationException ex)
            {
                // QueueAsync raises these with a message written for the user —
                // no brief chosen, prompt not configured, not enough credit.
                return BadRequest(new { success = false, message = ex.Message });
            }
        }

        [HttpGet("job/{id}")]
        public async Task<IActionResult> GetJob(int id, [FromQuery] int clientId)
        {
            var job = await _context.contact_validation_jobs
                .AsNoTracking()
                .FirstOrDefaultAsync(j => j.Id == id && j.ClientId == clientId);

            if (job == null)
                return NotFound(new { success = false, message = "No such validation run." });

            return Ok(new { success = true, job = ContactValidationService.ToDto(job) });
        }

        /// <summary>
        /// The cost log: what each run consumed and what it cost. This is the
        /// table the searches-per-100 figure comes out of, which is what credit
        /// pricing has to be set from.
        /// </summary>
        [HttpGet("jobs")]
        public async Task<IActionResult> GetJobs([FromQuery] int clientId, [FromQuery] int take = 50)
        {
            if (clientId <= 0)
                return BadRequest(new { success = false, message = "clientId must be greater than 0." });

            var jobs = await _context.contact_validation_jobs
                .AsNoTracking()
                .Where(j => j.ClientId == clientId)
                .OrderByDescending(j => j.CreatedAt)
                .Take(Math.Clamp(take, 1, 200))
                .ToListAsync();

            return Ok(new
            {
                success = true,
                jobs = jobs.Select(ContactValidationService.ToDto)
            });
        }

        // =============================================================
        // Results
        // =============================================================

        [HttpGet("results")]
        public async Task<IActionResult> GetResults(
            [FromQuery] int clientId,
            [FromQuery] string? contactIds = null)
        {
            if (clientId <= 0)
                return BadRequest(new { success = false, message = "clientId must be greater than 0." });

            var query = _context.contact_validations
                .AsNoTracking()
                .Where(v => v.ClientId == clientId);

            if (!string.IsNullOrWhiteSpace(contactIds))
            {
                var ids = contactIds
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(value => int.TryParse(value, out var id) ? id : 0)
                    .Where(id => id > 0)
                    .ToList();

                if (ids.Count == 0)
                    return Ok(new { success = true, results = Array.Empty<ContactValidationDto>() });

                query = query.Where(v => ids.Contains(v.ContactId));
            }

            var rows = await query.ToListAsync();

            return Ok(new { success = true, results = rows.Select(ToDto) });
        }

        [HttpPost("mark-verified")]
        public async Task<IActionResult> MarkVerified([FromBody] MarkVerifiedRequestDto request)
        {
            if (request == null || request.ClientId <= 0)
                return BadRequest(new { success = false, message = "A valid client is required." });

            var ids = (request.ContactIds ?? new List<int>()).Where(id => id > 0).Distinct().ToList();

            if (ids.Count == 0)
                return BadRequest(new { success = false, message = "Select at least one contact." });

            var existing = await _context.contact_validations
                .Where(v => v.ClientId == request.ClientId && ids.Contains(v.ContactId))
                .ToListAsync();

            var byContact = existing.ToDictionary(v => v.ContactId);
            var now = DateTime.UtcNow;

            foreach (var contactId in ids)
            {
                if (!byContact.TryGetValue(contactId, out var row))
                {
                    // A contact can be marked verified before any check has run
                    // — the user has confirmed it themselves — so the row is
                    // created with the scores left null.
                    row = new ContactValidation
                    {
                        ClientId = request.ClientId,
                        ContactId = contactId,
                        CreatedAt = now
                    };

                    _context.contact_validations.Add(row);
                }

                row.IsVerified = request.IsVerified;
                row.VerifiedAt = request.IsVerified ? now : null;
                row.VerifiedBy = request.IsVerified ? request.VerifiedBy : null;
                row.UpdatedAt = now;

                if (request.IsVerified)
                {
                    // Marking a contact verified is a person saying they have
                    // checked the record themselves, so every check that has
                    // actually run is stored at 100 — their judgement outranks
                    // the model's, and the score is what the grid sorts,
                    // filters and exports on, so it has to be the one that
                    // carries the verdict.
                    //
                    // A check that never ran keeps its null. Writing 100 there
                    // would claim an email had been validated when no email
                    // check has ever been run against it, which is the one
                    // thing a confidence score must never do.
                    //
                    // This overwrites the model's number rather than shadowing
                    // it: removing the mark afterwards leaves the 100s in
                    // place, and only re-running a check produces a fresh
                    // score.
                    if (row.ContactFitConfidence.HasValue)
                        row.ContactFitConfidence = 100;

                    if (row.DataIntegrityConfidence.HasValue)
                        row.DataIntegrityConfidence = 100;

                    if (row.LiveContactConfidence.HasValue)
                        row.LiveContactConfidence = 100;

                    if (row.EmailValidityConfidence.HasValue)
                        row.EmailValidityConfidence = 100;
                }
            }

            await _context.SaveChangesAsync();

            return Ok(new
            {
                success = true,
                message = request.IsVerified
                    ? $"{ids.Count} contact(s) marked as verified."
                    : $"The verified mark was removed from {ids.Count} contact(s)."
            });
        }

        private static ContactValidationDto ToDto(ContactValidation row)
        {
            var sources = new List<ValidationSourceDto>();

            if (!string.IsNullOrWhiteSpace(row.SourcesJson))
            {
                try
                {
                    sources = JsonConvert.DeserializeObject<List<ValidationSourceDto>>(row.SourcesJson)
                              ?? new List<ValidationSourceDto>();
                }
                catch (JsonException)
                {
                    // A malformed sources blob must not take the whole row's
                    // scores down with it.
                    sources = new List<ValidationSourceDto>();
                }
            }

            return new ContactValidationDto
            {
                ContactId = row.ContactId,
                ContactFitConfidence = row.ContactFitConfidence,
                ContactFitComments = row.ContactFitComments,
                ContactFitBriefId = row.ContactFitBriefId,
                ContactFitCheckedAt = row.ContactFitCheckedAt,
                DataIntegrityConfidence = row.DataIntegrityConfidence,
                DataIntegrityComments = row.DataIntegrityComments,
                DataIntegrityCheckedAt = row.DataIntegrityCheckedAt,
                LiveContactConfidence = row.LiveContactConfidence,
                LiveContactComments = row.LiveContactComments,
                LiveContactCheckedAt = row.LiveContactCheckedAt,
                EmailValidityConfidence = row.EmailValidityConfidence,
                EmailValidityStatus = row.EmailValidityStatus,
                EmailValiditySource = row.EmailValiditySource,
                EmailValidityComments = row.EmailValidityComments,
                EmailCheckedAt = row.EmailCheckedAt,
                Sources = sources,
                IsVerified = row.IsVerified,
                VerifiedAt = row.VerifiedAt,
                VerifiedBy = row.VerifiedBy
            };
        }

        // =============================================================
        // Metadata for the run panel
        // =============================================================

        /// <summary>
        /// The four checks with their labels, so the run panel does not have to
        /// keep its own copy of the descriptions.
        /// </summary>
        [HttpGet("check-types")]
        public IActionResult GetCheckTypes() => Ok(new
        {
            success = true,
            checkTypes = ValidationCheckTypes.All.Select(key =>
            {
                var (label, description) = ValidationCheckTypes.Describe(key);

                return new
                {
                    key,
                    label,
                    description,
                    requiresBrief = ValidationCheckTypes.RequiresBrief(key),
                    usesWebSearch = ValidationCheckTypes.UsesWebSearch(key)
                };
            })
        });
    }
}
