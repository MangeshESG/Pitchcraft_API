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

                    // Each raised score is marked as hand-set, so a cell can
                    // say where its 100 came from. Only checks that actually
                    // moved are marked: one that never ran has no score for the
                    // note to explain.
                    foreach (var check in ValidationCheckTypes.All)
                    {
                        if (row.ConfidenceFor(check).HasValue)
                        {
                            row.ManualScoresJson = ManualScoreMarks.Set(
                                row.ManualScoresJson, check, now, request.VerifiedBy);
                        }
                    }
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

        /// <summary>
        /// Sets one check's score to 100, for a user overruling that verdict
        /// alone.
        /// </summary>
        /// <remarks>
        /// Distinct from mark-verified, which speaks for the whole contact and
        /// raises all four checks. A user who has looked at a data integrity
        /// score of 40, decided the record is actually fine and wants to move
        /// on should not thereby be claiming the email address was validated —
        /// so this writes one column and leaves the rest alone.
        ///
        /// It does not touch any pending corrections. Saying a score is
        /// acceptable and wanting a name typo fixed are not the same decision,
        /// and folding them together would mean one click silently discarding
        /// the other.
        /// </remarks>
        [HttpPost("score/verify")]
        public async Task<IActionResult> VerifyScore([FromBody] VerifyScoreRequestDto request)
        {
            if (request == null || request.ClientId <= 0 || request.ContactId <= 0)
                return BadRequest(new { success = false, message = "A valid client and contact are required." });

            var checkType = ValidationCheckTypes.Normalize(request.CheckType ?? "");

            if (!ValidationCheckTypes.IsKnown(checkType))
                return BadRequest(new { success = false, message = "Unknown check." });

            var row = await _context.contact_validations
                .FirstOrDefaultAsync(v =>
                    v.ClientId == request.ClientId && v.ContactId == request.ContactId);

            if (row == null)
                return NotFound(new { success = false, message = "This contact has not been validated." });

            var verifiedAt = DateTime.UtcNow;

            row.SetConfidence(checkType, 100);

            // A hand-set 100 is indistinguishable from an earned one once it is
            // in the column, so the mark is what lets the cell say which it is.
            row.ManualScoresJson = ManualScoreMarks.Set(
                row.ManualScoresJson, checkType, verifiedAt, request.VerifiedBy);

            row.UpdatedAt = verifiedAt;
            await _context.SaveChangesAsync();

            var (label, _) = ValidationCheckTypes.Describe(checkType);

            return Ok(new
            {
                success = true,
                message = $"{label} set to 100.",
                checkType,
                score = 100,
                verifiedAt,
                manualScores = ManualScoreMarks.Read(row.ManualScoresJson)
            });
        }

        /// <summary>
        /// Sets one check's score to 100 across many contacts at once — the
        /// bulk update panel's version of the single-score override.
        /// </summary>
        /// <remarks>
        /// One check, many contacts, which is the opposite axis to
        /// mark-verified: that one speaks for a whole contact and raises all
        /// four of its scores. A user working down an Employment Match column
        /// who can see that fifty rows are fine is making one judgement about
        /// one check, and should not have to claim their email addresses were
        /// validated to record it.
        ///
        /// Contacts with no validation row yet get one. The judgement is the
        /// user's own and does not depend on a run having happened first, and
        /// the mark written beside the score says plainly that no run produced
        /// it — the other three checks stay null, so nothing claims to have
        /// been checked that has not been.
        ///
        /// Pending corrections are left alone, for the same reason the
        /// single-contact override leaves them: deciding a score is acceptable
        /// and wanting a typo fixed are two different decisions, and folding
        /// them together would have one click silently discard the other.
        /// </remarks>
        [HttpPost("score/verify-bulk")]
        public async Task<IActionResult> VerifyScores([FromBody] VerifyScoresRequestDto request)
        {
            if (request == null || request.ClientId <= 0)
                return BadRequest(new { success = false, message = "A valid client is required." });

            var checkType = ValidationCheckTypes.Normalize(request.CheckType ?? "");

            if (!ValidationCheckTypes.IsKnown(checkType))
                return BadRequest(new { success = false, message = "Unknown check." });

            var ids = (request.ContactIds ?? new List<int>())
                .Where(id => id > 0)
                .Distinct()
                .ToList();

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
                    row = new ContactValidation
                    {
                        ClientId = request.ClientId,
                        ContactId = contactId,
                        CreatedAt = now
                    };

                    _context.contact_validations.Add(row);
                }

                row.SetConfidence(checkType, 100);
                row.ManualScoresJson = ManualScoreMarks.Set(
                    row.ManualScoresJson, checkType, now, request.VerifiedBy);
                row.UpdatedAt = now;
            }

            await _context.SaveChangesAsync();

            var (bulkLabel, _) = ValidationCheckTypes.Describe(checkType);

            return Ok(new
            {
                success = true,
                message = $"{bulkLabel} set to 100 for {ids.Count} contact(s).",
                checkType,
                score = 100,
                verifiedAt = now,
                updated = ids.Count
            });
        }

        // =============================================================
        // Suggested corrections
        // =============================================================

        /// <summary>
        /// Applies one suggested correction to the contact and marks the
        /// suggestion accepted. Works for any of the four checks; the body says
        /// which one's list the id belongs to.
        /// </summary>
        /// <remarks>
        /// Both halves happen here, in one save, rather than the browser
        /// calling <c>Crm/update-contact</c> and then a second endpoint to
        /// record the outcome. Two reasons.
        ///
        /// First, <c>update-contact</c> writes the whole record from the DTO it
        /// is given, so every field the caller omits is written as null. The
        /// grid row behind an Accept button does not carry every field, and
        /// accepting a job-title fix from it would blank the telephone number
        /// and the industry on the way past. This writes one column.
        ///
        /// Second, the field being written has to be one of the small set the
        /// check is allowed to correct, and that has to be decided on this side
        /// of the wire - the suggestion arrives from a language model, and its
        /// field name is only a claim until something checks it against
        /// <see cref="ValidationSuggestionFields"/>.
        ///
        /// The response returns the applied field and value so the caller can
        /// patch the one row it has on screen instead of refetching the list.
        /// </remarks>
        [HttpPost("suggestions/accept")]
        public Task<IActionResult> AcceptSuggestion(
            [FromBody] ResolveSuggestionRequestDto request) =>
            ResolveSuggestionAsync(request, accept: true);

        /// <summary>
        /// Marks a suggestion dismissed without touching the contact.
        ///
        /// It stays on the row rather than being deleted: a user who dismissed
        /// a correction and wants to see what it was still can, and the record
        /// of having decided is what stops the same suggestion reading as
        /// unreviewed to the next person looking at the contact.
        /// </summary>
        [HttpPost("suggestions/dismiss")]
        public Task<IActionResult> DismissSuggestion(
            [FromBody] ResolveSuggestionRequestDto request) =>
            ResolveSuggestionAsync(request, accept: false);

        /// <summary>
        /// Applies every correction one check still has pending on one contact,
        /// in a single save.
        /// </summary>
        /// <remarks>
        /// The per-suggestion endpoint exists because accepting a correction is
        /// a write to a customer record and each one deserves to be read first.
        /// This one exists because that stops being true the moment a run comes
        /// back with eight proposals for the same contact: an enhancement run
        /// is asked for several columns at once, and clicking Accept eight
        /// times - each a round trip, each re-patching the same row - is the
        /// review happening once and the clicking happening eight times.
        ///
        /// Every proposal is still applied through the same checks as a single
        /// Accept, one at a time: a field that no longer resolves is left
        /// pending and reported, rather than failing the other seven with it.
        /// </remarks>
        [HttpPost("suggestions/accept-all")]
        public async Task<IActionResult> AcceptAllSuggestions(
            [FromBody] ResolveSuggestionRequestDto request)
        {
            if (request == null || request.ClientId <= 0 || request.ContactId <= 0)
                return BadRequest(new { success = false, message = "A valid client and contact are required." });

            var checkType = ValidationCheckTypes.Normalize(
                request.CheckType ?? ValidationCheckTypes.DataIntegrity);

            if (!ValidationCheckTypes.IsKnown(checkType))
                return BadRequest(new { success = false, message = "Unknown check." });

            var row = await _context.contact_validations
                .FirstOrDefaultAsync(v =>
                    v.ClientId == request.ClientId && v.ContactId == request.ContactId);

            if (row == null)
                return NotFound(new { success = false, message = "This contact has not been validated." });

            var suggestions = ValidationSuggestionJson.Deserialize(
                row.SuggestionsJsonFor(checkType));

            var pending = suggestions
                .Where(item => item.Status == ValidationSuggestionStatuses.Pending)
                .ToList();

            if (pending.Count == 0)
            {
                return BadRequest(new
                {
                    success = false,
                    message = "There is nothing left to accept on this contact."
                });
            }

            var contact = await _context.contacts
                .FirstOrDefaultAsync(c =>
                    c.id == request.ContactId &&
                    _context.data_files.Any(f =>
                        f.id == c.DataFileId && f.client_id == request.ClientId));

            if (contact == null)
                return NotFound(new { success = false, message = "Contact not found." });

            var now = DateTime.UtcNow;
            var applied = new List<object>();
            var refused = new List<string>();

            foreach (var suggestion in pending)
            {
                object? outcome;

                if (checkType == ValidationCheckTypes.DataEnhancement)
                {
                    outcome = await ApplyEnhancementValueAsync(
                        request.ClientId, contact, suggestion, now);
                }
                else if (ValidationSuggestionFields.IsWritableBy(checkType, suggestion.Field) &&
                         ValidationSuggestionFields.Apply(contact, suggestion.Field, suggestion.Suggested))
                {
                    outcome = new
                    {
                        field = suggestion.Field,
                        columnKey = suggestion.Field,
                        value = suggestion.Suggested,
                        fullName = contact.full_name,
                        firstName = contact.first_name,
                        lastName = contact.last_name,
                        customFieldId = (int?)null,
                        customFieldName = (string?)null
                    };
                }
                else
                {
                    outcome = null;
                }

                if (outcome == null)
                {
                    // Left pending on purpose. The user can still see what it
                    // was and decide, which is more use than a correction that
                    // quietly disappeared into a bulk action.
                    refused.Add(SuggestionLabel(checkType, suggestion));
                    continue;
                }

                applied.Add(outcome);

                suggestion.Status = ValidationSuggestionStatuses.Accepted;
                suggestion.ResolvedAt = now;
                suggestion.ResolvedBy = request.ResolvedBy;
            }

            if (applied.Count == 0)
            {
                return BadRequest(new
                {
                    success = false,
                    message = "None of these corrections could be applied: " +
                              string.Join(", ", refused) +
                              " no longer name columns this check can write."
                });
            }

            contact.updated_at = now;

            row.SetSuggestionsJson(checkType, ValidationSuggestionJson.Serialize(suggestions));
            row.UpdatedAt = now;

            await _context.SaveChangesAsync();

            var message = $"{applied.Count} correction{(applied.Count == 1 ? "" : "s")} applied.";

            if (refused.Count > 0)
                message += $" {string.Join(", ", refused)} could not be written and {(refused.Count == 1 ? "is" : "are")} still waiting.";

            return Ok(new
            {
                success = true,
                message,
                applied,
                checkType,
                suggestions,
                suggestionsJson = row.SuggestionsJsonFor(checkType)
            });
        }

        private async Task<IActionResult> ResolveSuggestionAsync(
            ResolveSuggestionRequestDto request,
            bool accept)
        {
            if (request == null || request.ClientId <= 0 || request.ContactId <= 0)
                return BadRequest(new { success = false, message = "A valid client and contact are required." });

            if (string.IsNullOrWhiteSpace(request.SuggestionId))
                return BadRequest(new { success = false, message = "No suggestion was identified." });

            var checkType = ValidationCheckTypes.Normalize(
                request.CheckType ?? ValidationCheckTypes.DataIntegrity);

            if (!ValidationCheckTypes.IsKnown(checkType))
                return BadRequest(new { success = false, message = "Unknown check." });

            var row = await _context.contact_validations
                .FirstOrDefaultAsync(v =>
                    v.ClientId == request.ClientId && v.ContactId == request.ContactId);

            if (row == null)
                return NotFound(new { success = false, message = "This contact has not been validated." });

            var suggestions = ValidationSuggestionJson.Deserialize(
                row.SuggestionsJsonFor(checkType));

            var suggestion = suggestions.FirstOrDefault(
                item => string.Equals(item.Id, request.SuggestionId, StringComparison.OrdinalIgnoreCase));

            if (suggestion == null)
            {
                // Most often the check was re-run since the grid loaded, which
                // replaced the list. Saying so is more use than "not found",
                // because the fix is to reload rather than to try again.
                return NotFound(new
                {
                    success = false,
                    message = "That suggestion is no longer on this contact. Reload the list to see the current ones."
                });
            }

            if (suggestion.Status != ValidationSuggestionStatuses.Pending)
            {
                return BadRequest(new
                {
                    success = false,
                    message = $"This suggestion has already been {suggestion.Status}."
                });
            }

            var now = DateTime.UtcNow;
            object? applied = null;

            if (accept)
            {
                // Scoped to the client's own data files: a suggestion id from
                // one client's validation row must not be able to name another
                // client's contact.
                var contact = await _context.contacts
                    .FirstOrDefaultAsync(c =>
                        c.id == request.ContactId &&
                        _context.data_files.Any(f =>
                            f.id == c.DataFileId && f.client_id == request.ClientId));

                if (contact == null)
                    return NotFound(new { success = false, message = "Contact not found." });

                if (checkType == ValidationCheckTypes.DataEnhancement)
                {
                    applied = await ApplyEnhancementValueAsync(
                        request.ClientId, contact, suggestion, now);

                    if (applied == null)
                    {
                        return BadRequest(new
                        {
                            success = false,
                            message =
                                $"{suggestion.Label ?? suggestion.Field} is no longer a column this " +
                                "client has, so the value could not be written."
                        });
                    }
                }
                else
                {
                    // Checked again here rather than trusting what was stored:
                    // the field sets are what stop one check rewriting
                    // another's evidence, and a row written before a set was
                    // narrowed must not still be applyable.
                    if (!ValidationSuggestionFields.IsWritableBy(checkType, suggestion.Field) ||
                        !ValidationSuggestionFields.Apply(contact, suggestion.Field, suggestion.Suggested))
                    {
                        return BadRequest(new
                        {
                            success = false,
                            message = $"{suggestion.Field} is not a field this check can correct."
                        });
                    }

                    applied = new
                    {
                        field = suggestion.Field,
                        // The seven fields these checks write are all named
                        // after their database column, so the key is already
                        // the name the row carries. Sent anyway, so a caller
                        // patching a row never has to know which checks that
                        // is true of.
                        columnKey = suggestion.Field,
                        value = suggestion.Suggested,
                        // The name write also rebuilds the two split columns,
                        // and the grid shows those rather than full_name, so it
                        // needs all three back or the row it patches goes
                        // stale.
                        fullName = contact.full_name,
                        firstName = contact.first_name,
                        lastName = contact.last_name,
                        customFieldId = (int?)null,
                        customFieldName = (string?)null
                    };
                }

                contact.updated_at = now;
            }

            suggestion.Status = accept
                ? ValidationSuggestionStatuses.Accepted
                : ValidationSuggestionStatuses.Dismissed;
            suggestion.ResolvedAt = now;
            suggestion.ResolvedBy = request.ResolvedBy;

            row.SetSuggestionsJson(checkType, ValidationSuggestionJson.Serialize(suggestions));
            row.UpdatedAt = now;

            await _context.SaveChangesAsync();

            return Ok(new
            {
                success = true,
                message = accept
                    ? $"{SuggestionLabel(checkType, suggestion)} updated."
                    : "Suggestion dismissed.",
                applied,
                checkType,
                // The whole list back, serialised the way the grid stores it,
                // so one row can be patched from this response alone.
                suggestions,
                suggestionsJson = row.SuggestionsJsonFor(checkType)
            });
        }

        /// <summary>
        /// How a suggestion is named in the message the user reads. The
        /// enhancement check's fields can be the client's own attributes, whose
        /// names are not compiled in anywhere, so it uses the label the run
        /// stored beside the suggestion.
        /// </summary>
        private static string SuggestionLabel(
            string checkType,
            ValidationSuggestionDto suggestion) =>
            checkType == ValidationCheckTypes.DataEnhancement
                ? suggestion.Label ?? suggestion.Field
                : ValidationSuggestionFields.Label(suggestion.Field);

        /// <summary>
        /// Writes one accepted enhancement value, to a contact column or to a
        /// custom attribute, and returns what the grid needs to patch its row.
        /// Null when the column no longer exists or is not one this check may
        /// write.
        /// </summary>
        /// <remarks>
        /// The field name is re-resolved against the client's live attributes
        /// rather than trusted from the stored suggestion, for the same reason
        /// the other four checks re-check their field sets here: what is in the
        /// column is a language model's claim until something on this side of
        /// the wire has checked it. Resolving it against this client's own
        /// attributes is also what stops a suggestion row naming
        /// <c>custom:17</c> from reaching another client's attribute 17.
        /// </remarks>
        private async Task<object?> ApplyEnhancementValueAsync(
            int clientId,
            PitchGenApi.Models.Contact contact,
            ValidationSuggestionDto suggestion,
            DateTime now)
        {
            var value = suggestion.Suggested?.Trim();

            if (string.IsNullOrWhiteSpace(value)) return null;

            if (!DataEnhancementFields.TryParseCustomKey(suggestion.Field, out var customFieldId))
            {
                var key = DataEnhancementFields.NormalizeSystem(suggestion.Field);

                if (key == null || !DataEnhancementFields.ApplySystem(contact, key, value))
                    return null;

                return new
                {
                    field = key,
                    // Not the same string as the key for the four company
                    // columns: they reach the browser camel-cased, and a patch
                    // written under the key would change nothing on screen.
                    columnKey = DataEnhancementFields.RowKeyFor(key),
                    value,
                    fullName = contact.full_name,
                    firstName = contact.first_name,
                    lastName = contact.last_name,
                    customFieldId = (int?)null,
                    customFieldName = (string?)null
                };
            }

            var customField = await _context.crm_custom_fields
                .FirstOrDefaultAsync(field =>
                    field.id == customFieldId && field.client_id == clientId);

            if (customField == null) return null;

            var existing = await _context.contact_custom_field_values
                .FirstOrDefaultAsync(row =>
                    row.contact_id == contact.id && row.field_id == customField.id);

            if (existing == null)
            {
                _context.contact_custom_field_values.Add(new ContactCustomFieldValue
                {
                    client_id = clientId,
                    contact_id = contact.id,
                    field_id = customField.id,
                    value = value,
                    created_at = now
                });
            }
            else
            {
                existing.value = value;
            }

            return new
            {
                field = suggestion.Field,
                // A custom attribute has no column of its own on the row; the
                // name below is where it actually lives.
                columnKey = (string?)null,
                value,
                fullName = contact.full_name,
                firstName = contact.first_name,
                lastName = contact.last_name,
                customFieldId = (int?)customField.id,
                // The grid keys a row's custom values by the attribute's name,
                // not its id, so the name is what a patch needs to find the
                // cell it has just changed.
                customFieldName = customField.field_name
            };
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
                DataEnhancementConfidence = row.DataEnhancementConfidence,
                DataEnhancementComments = row.DataEnhancementComments,
                DataEnhancementCheckedAt = row.DataEnhancementCheckedAt,
                Sources = sources,
                ContactFitSuggestions = ValidationSuggestionJson.Deserialize(row.ContactFitSuggestionsJson),
                DataIntegritySuggestions = ValidationSuggestionJson.Deserialize(row.DataIntegritySuggestionsJson),
                LiveContactSuggestions = ValidationSuggestionJson.Deserialize(row.LiveContactSuggestionsJson),
                EmailValiditySuggestions = ValidationSuggestionJson.Deserialize(row.EmailValiditySuggestionsJson),
                DataEnhancementSuggestions = ValidationSuggestionJson.Deserialize(row.DataEnhancementSuggestionsJson),
                IsVerified = row.IsVerified,
                VerifiedAt = row.VerifiedAt,
                VerifiedBy = row.VerifiedBy,
                ManualScores = ManualScoreMarks.Read(row.ManualScoresJson)
            };
        }

        // =============================================================
        // Metadata for the run panel
        // =============================================================

        /// <summary>
        /// The four checks with their labels, so the run panel does not have to
        /// keep its own copy of the descriptions.
        /// </summary>
        /// <summary>
        /// The columns a Customisable Data Enhancement run can be pointed at:
        /// the contact's own, then this client's custom attributes.
        /// </summary>
        /// <remarks>
        /// Served from here rather than assembled in the browser from the CRM's
        /// column endpoints, because the keys have to be the ones the run will
        /// accept. The picker offering a column the runner would then drop is
        /// the failure this endpoint exists to make impossible: one list, built
        /// from the same <see cref="EnhancementFieldSet"/> the run resolves
        /// against.
        ///
        /// The custom attributes are listed in the order the client arranged
        /// them, so the picker reads like their own grid rather than like the
        /// database.
        /// </remarks>
        [HttpGet("enhancement-fields")]
        public async Task<IActionResult> GetEnhancementFields([FromQuery] int clientId)
        {
            if (clientId <= 0)
                return BadRequest(new { success = false, message = "A valid client is required." });

            var fields = DataEnhancementFields.SystemFields
                .Select(field => new EnhancementFieldDto
                {
                    Key = field.Key,
                    Label = field.Label,
                    Description = field.Description,
                    IsCustom = false
                })
                .ToList();

            var customFields = await _context.crm_custom_fields
                .AsNoTracking()
                .Where(field => field.client_id == clientId)
                .OrderBy(field => field.sort_order)
                .ThenBy(field => field.id)
                .ToListAsync();

            var custom = EnhancementFieldSet.Build(
                customFields.Select(field => DataEnhancementFields.CustomKey(field.id)),
                customFields);

            fields.AddRange(custom.Fields.Select(field => new EnhancementFieldDto
            {
                Key = field.Key,
                Label = field.Label,
                Description = field.Description,
                IsCustom = true,
                CustomFieldId = field.CustomFieldId,
                FieldType = field.FieldType,
                Options = field.Options.ToList()
            }));

            return Ok(new { success = true, fields });
        }

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
                    usesWebSearch = ValidationCheckTypes.UsesWebSearch(key),
                    // True for exactly one check, and it is what tells the run
                    // panel to show the column picker and the instruction box
                    // instead of starting the run on the next click.
                    requiresFields = ValidationCheckTypes.RequiresFields(key)
                };
            })
        });
    }
}
