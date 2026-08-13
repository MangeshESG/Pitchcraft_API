namespace PitchGenApi.Services
{
    using Microsoft.EntityFrameworkCore;
    using PitchGenApi.Database;
    using PitchGenApi.Interfaces;
    using PitchGenApi.Model;
    using PitchGenApi.Model.DTOs;
    using PitchGenApi.Models;

    /// <summary>
    /// Everything the LinkedIn browser extension needs that is not email
    /// unlocking: the "already in Pitchkraft?" lookup, the create/patch save and
    /// the LLM professional summary.
    /// </summary>
    public class ExtensionProfileService : IExtensionProfileService
    {
        private const int SummaryProfileTextLimit = 24000;

        private readonly AppDbContext _context;
        private readonly IAiModelSettingsService _aiModelSettings;
        private readonly IPitchService _pitchService;
        private readonly DeepSeekPitchService _deepSeekService;

        public ExtensionProfileService(
            AppDbContext context,
            IAiModelSettingsService aiModelSettings,
            IPitchService pitchService,
            DeepSeekPitchService deepSeekService)
        {
            _context = context;
            _aiModelSettings = aiModelSettings;
            _pitchService = pitchService;
            _deepSeekService = deepSeekService;
        }

        // ---------------------------------------------------------------- lookup

        public async Task<ExtensionOperationResult> GetProfileContextAsync(
            ExtensionProfileContextRequestDto request)
        {
            var lists = await _context.data_files
                .AsNoTracking()
                .Where(df => df.client_id == request.ClientId)
                .Select(df => new
                {
                    df.id,
                    df.name,
                    df.data_file_name,
                    contactCount = _context.contacts.Count(c => c.DataFileId == df.id)
                })
                .ToListAsync();

            var orderedLists = lists
                .Select(list => new
                {
                    list.id,
                    name = string.IsNullOrWhiteSpace(list.name)
                        ? list.data_file_name
                        : list.name,
                    list.contactCount
                })
                .OrderBy(list => list.name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var match = await FindContactByLinkedInUrlAsync(
                request.ClientId,
                request.LinkedInUrl);

            if (match == null)
            {
                return new ExtensionOperationResult(200, new
                {
                    exists = false,
                    contact = (object?)null,
                    lists = orderedLists
                });
            }

            return new ExtensionOperationResult(200, new
            {
                exists = true,
                contact = DescribeContact(match.Contact, match.DataFile),
                lists = orderedLists
            });
        }

        // ------------------------------------------------------------------ save

        public async Task<ExtensionOperationResult> SaveProfileAsync(
            ExtensionSaveProfileRequestDto request)
        {
            var fields = request.Fields ?? new ExtensionContactFieldsDto();

            return request.ContactId.HasValue && request.ContactId.Value > 0
                ? await UpdateContactAsync(request, fields)
                : await CreateContactAsync(request, fields);
        }

        private async Task<ExtensionOperationResult> UpdateContactAsync(
            ExtensionSaveProfileRequestDto request,
            ExtensionContactFieldsDto fields)
        {
            var contact = await (
                from c in _context.contacts
                join df in _context.data_files on c.DataFileId equals (int?)df.id
                where c.id == request.ContactId!.Value && df.client_id == request.ClientId
                select c).FirstOrDefaultAsync();

            if (contact == null)
            {
                return new ExtensionOperationResult(404, new
                {
                    message = "Contact was not found in any of this client's lists."
                });
            }

            var updatedFields = ApplyFields(contact, fields);

            if (updatedFields.Count == 0)
            {
                return new ExtensionOperationResult(400, new
                {
                    message = "Select at least one field to save."
                });
            }

            contact.updated_at = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            var dataFile = await _context.data_files
                .AsNoTracking()
                .FirstOrDefaultAsync(df => df.id == contact.DataFileId);

            return new ExtensionOperationResult(200, new
            {
                success = true,
                created = false,
                updatedFields,
                contact = DescribeContact(contact, dataFile)
            });
        }

        private async Task<ExtensionOperationResult> CreateContactAsync(
            ExtensionSaveProfileRequestDto request,
            ExtensionContactFieldsDto fields)
        {
            if (!request.DataFileId.HasValue || request.DataFileId.Value <= 0)
            {
                return new ExtensionOperationResult(400, new
                {
                    message = "Choose a list before saving the contact."
                });
            }

            if (string.IsNullOrWhiteSpace(fields.LinkedInUrl))
            {
                return new ExtensionOperationResult(400, new
                {
                    message = "A LinkedIn URL is required."
                });
            }

            // A contact is only worth storing once we can email them, so the
            // panel keeps Save disabled until Unlock has returned an address.
            if (string.IsNullOrWhiteSpace(fields.Email))
            {
                return new ExtensionOperationResult(400, new
                {
                    message = "Unlock the email address before saving this contact."
                });
            }

            var dataFile = await _context.data_files
                .FirstOrDefaultAsync(df =>
                    df.id == request.DataFileId.Value &&
                    df.client_id == request.ClientId);

            if (dataFile == null)
            {
                return new ExtensionOperationResult(404, new
                {
                    message = "That list was not found for this client."
                });
            }

            // The same LinkedIn profile must exist only once across all of the
            // client's lists.
            var duplicate = await FindContactByLinkedInUrlAsync(
                request.ClientId,
                fields.LinkedInUrl);

            if (duplicate != null)
            {
                return new ExtensionOperationResult(409, new
                {
                    message = "This LinkedIn contact already exists in another Pitchkraft list.",
                    contact = DescribeContact(duplicate.Contact, duplicate.DataFile)
                });
            }

            var contact = new Contact
            {
                DataFileId = dataFile.id,
                linkedin_url = fields.LinkedInUrl!.Trim(),
                created_at = DateTime.UtcNow
            };

            ApplyFields(contact, fields);
            SplitName(contact);

            _context.contacts.Add(contact);
            await _context.SaveChangesAsync();

            return new ExtensionOperationResult(200, new
            {
                success = true,
                created = true,
                contact = DescribeContact(contact, dataFile)
            });
        }

        // --------------------------------------------------------------- summary -------------------------------------------

        public async Task<ExtensionOperationResult> GenerateProfileSummaryAsync(
            ExtensionProfileSummaryRequestDto request,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(request.ProfileText))
            {
                return new ExtensionOperationResult(400, new
                {
                    message = "No profile content was captured from LinkedIn."
                });
            }

            if (request.ClientId <= 0)
            {
                return new ExtensionOperationResult(400, new
                {
                    message = "A valid ClientId is required."
                });
            }

            Contact? contact = null;

            if (request.Save)
            {
                contact = await ResolveSummaryTargetAsync(request);

                if (contact == null)
                {
                    return new ExtensionOperationResult(404, new
                    {
                        message = "Save the contact into a list before storing a summary."
                    });
                }
            }

            var modelName = await _aiModelSettings.GetModelAsync(AiModelPurposes.ProfileSummary);
            var isDeepSeek = IsDeepSeekModel(modelName);

            var profileText = request.ProfileText!.Trim();

            if (profileText.Length > SummaryProfileTextLimit)
                profileText = profileText[..SummaryProfileTextLimit];

            // The instruction is fixed in code — only the profile text and the
            // identifying fields come from the extension.
            var enquiryRequest = new EnquiryRequest
            {
                Prompt = ProfileSummaryPrompt.Build(
                    request.ContactName,
                    request.CompanyName,
                    request.LinkedInUrl,
                    profileText),
                ScrappedData = ProfileSummaryPrompt.Instruction,
                ModelName = modelName
            };

            PitchResult result;

            // Same path as find-email: DeepSeek or OpenAI depending on the model
            // an admin chose for this purpose. Both deduct the one credit for the
            // client they are handed.
            try
            {
                result = isDeepSeek
                    ? await _deepSeekService.GenerateWebSearchAsync(enquiryRequest, request.ClientId)
                    : await _pitchService.GenerateWebSearchAsync(enquiryRequest, request.ClientId);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new ExtensionOperationResult(502, new
                {
                    message = "The summary could not be generated: " + ex.Message
                });
            }

            if (!result.IsSuccess)
            {
                return new ExtensionOperationResult(502, new
                {
                    message = "The summary could not be generated.",
                    model = modelName,
                    error = result.Content
                });
            }

            var summary = (result.Content ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(summary))
            {
                return new ExtensionOperationResult(502, new
                {
                    message = "The model returned an empty summary.",
                    model = modelName
                });
            }

            if (contact != null)
            {
                contact.linkedIninformation = summary;
                contact.updated_at = DateTime.UtcNow;
                await _context.SaveChangesAsync(cancellationToken);
            }

            return new ExtensionOperationResult(200, new
            {
                success = true,
                summary,
                saved = contact != null,
                contactId = contact?.id,
                model = modelName,
                provider = isDeepSeek ? "DeepSeek" : "OpenAI",
                usage = new
                {
                    result.PromptTokens,
                    result.CompletionTokens,
                    result.SearchTokens,
                    result.TotalTokens,
                    result.CurrentCost
                }
            });
        }

        private static bool IsDeepSeekModel(string? modelName)
            => modelName?.StartsWith("deepseek-", StringComparison.OrdinalIgnoreCase) == true;

        private async Task<Contact?> ResolveSummaryTargetAsync(
            ExtensionProfileSummaryRequestDto request)
        {
            if (request.ContactId.HasValue && request.ContactId.Value > 0)
            {
                return await (
                    from c in _context.contacts
                    join df in _context.data_files on c.DataFileId equals (int?)df.id
                    where c.id == request.ContactId.Value && df.client_id == request.ClientId
                    select c).FirstOrDefaultAsync();
            }

            var match = await FindContactByLinkedInUrlAsync(
                request.ClientId,
                request.LinkedInUrl,
                tracked: true);

            return match?.Contact;
        }

        // --------------------------------------------------------------- helpers -----------------------------------------------------

        private sealed record ContactMatch(Contact Contact, DataFile? DataFile);

        /// <summary>
        /// Finds the contact carrying this LinkedIn URL anywhere in the client's
        /// lists. Comparison is done in memory because the stored URLs vary in
        /// casing, trailing slash and locale prefix.
        /// </summary>
        private async Task<ContactMatch?> FindContactByLinkedInUrlAsync(
            int clientId,
            string? linkedInUrl,
            bool tracked = false)
        {
            var normalizedUrl = NormalizeLinkedInUrl(linkedInUrl);

            if (string.IsNullOrEmpty(normalizedUrl))
                return null;

            var query =
                from c in _context.contacts
                join df in _context.data_files on c.DataFileId equals (int?)df.id
                where df.client_id == clientId && c.linkedin_url != null
                select new { Contact = c, DataFile = df };

            // Narrow to the handful of rows that mention this vanity slug before
            // normalising in memory; stored URLs differ in casing, trailing slash
            // and locale subdomain, so SQL alone cannot decide the match.
            var slug = ExtractSlug(linkedInUrl);

            if (!string.IsNullOrEmpty(slug))
                query = query.Where(row => row.Contact.linkedin_url!.Contains("/in/" + slug));

            if (!tracked)
                query = query.AsNoTracking();

            var candidates = await query.ToListAsync();
            var match = candidates.FirstOrDefault(candidate =>
                NormalizeLinkedInUrl(candidate.Contact.linkedin_url) == normalizedUrl);

            return match == null ? null : new ContactMatch(match.Contact, match.DataFile);
        }

        private static readonly System.Text.RegularExpressions.Regex ProfileSlugPattern =
            new(@"linkedin\.com/in/([^/?#]+)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>The vanity slug of a LinkedIn profile URL, or empty.</summary>
        private static string ExtractSlug(string? value)
        {
            var match = ProfileSlugPattern.Match(value ?? string.Empty);
            return match.Success ? match.Groups[1].Value.Trim() : string.Empty;
        }

        /// <summary>
        /// Reduces a LinkedIn profile URL to its vanity slug so that
        /// "https://uk.linkedin.com/in/Tom-Duffy/?trk=nav" and
        /// "linkedin.com/in/tom-duffy" compare equal.
        /// </summary>
        private static string NormalizeLinkedInUrl(string? value)
        {
            var url = (value ?? string.Empty).Trim();

            if (url.Length == 0)
                return string.Empty;

            var slug = ExtractSlug(url);

            if (slug.Length > 0)
                return Uri.UnescapeDataString(slug).Trim().ToLowerInvariant();

            return url.TrimEnd('/').ToLowerInvariant();
        }

        /// <summary>
        /// Copies the supplied (non-null) fields onto the contact and reports
        /// which ones were written.
        /// </summary>
        private static List<string> ApplyFields(Contact contact, ExtensionContactFieldsDto fields)
        {
            var updated = new List<string>();

            void Set(string? value, string name, Action<string> assign)
            {
                if (value == null)
                    return;

                assign(value.Trim());
                updated.Add(name);
            }

            Set(fields.FullName, "fullName", value =>
            {
                contact.full_name = value;
                SplitName(contact);
            });
            Set(fields.JobTitle, "jobTitle", value => contact.job_title = value);
            Set(fields.Location, "location", value => contact.country_or_address = value);
            Set(fields.Email, "email", value => contact.email = value);
            Set(fields.LinkedInUrl, "linkedInUrl", value => contact.linkedin_url = value);
            Set(fields.CompanyName, "companyName", value => contact.company_name = value);
            Set(fields.Website, "website", value => contact.website = value);
            Set(fields.CompanyIndustry, "companyIndustry", value => contact.CompanyIndustry = value);
            Set(fields.CompanyEmployeeCount, "companyEmployeeCount",
                value => contact.CompanyEmployeeCount = value);
            Set(fields.CompanyLinkedInUrl, "companyLinkedInUrl",
                value => contact.CompanyLinkedInURL = value);

            return updated;
        }

        /// <summary>
        /// Keeps first_name/last_name in step with full_name, which the rest of
        /// the CRM relies on for personalisation placeholders.
        /// </summary>
        private static void SplitName(Contact contact)
        {
            var fullName = (contact.full_name ?? string.Empty).Trim();

            if (fullName.Length == 0)
                return;

            var parts = fullName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            contact.first_name = parts.FirstOrDefault();
            contact.last_name = parts.Length > 1 ? string.Join(" ", parts.Skip(1)) : string.Empty;
        }

        private static object DescribeContact(Contact contact, DataFile? dataFile) => new
        {
            contactId = contact.id,
            dataFileId = contact.DataFileId,
            dataFileName = dataFile == null
                ? null
                : string.IsNullOrWhiteSpace(dataFile.name) ? dataFile.data_file_name : dataFile.name,
            fullName = contact.full_name,
            jobTitle = contact.job_title,
            location = contact.country_or_address,
            email = contact.email,
            linkedInUrl = contact.linkedin_url,
            companyName = contact.company_name,
            website = contact.website,
            companyIndustry = contact.CompanyIndustry,
            companyEmployeeCount = contact.CompanyEmployeeCount,
            companyLinkedInUrl = contact.CompanyLinkedInURL,
            hasSummary = !string.IsNullOrWhiteSpace(contact.linkedIninformation)
        };
    }
}
