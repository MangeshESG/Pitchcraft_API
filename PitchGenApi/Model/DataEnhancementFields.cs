using PitchGenApi.Models;

namespace PitchGenApi.Model
{
    /// <summary>
    /// One column the Customisable Data Enhancement check can be pointed at:
    /// a contact column, or one of the client's own custom attributes.
    /// </summary>
    public sealed class EnhancementField
    {
        /// <summary>
        /// The canonical key, as it is stored on the job and written into every
        /// suggestion: a contact column name such as <c>job_title</c>, or
        /// <c>custom:17</c> for a custom attribute.
        ///
        /// Custom attributes are keyed by id rather than by name because the
        /// name is editable. A run saved against "Industry" has to keep
        /// meaning the same column after someone renames it to "Sector", and
        /// it must never start meaning a different column because a second
        /// attribute was given the old name.
        /// </summary>
        public string Key { get; init; } = "";

        /// <summary>What the column is called on screen.</summary>
        public string Label { get; init; } = "";

        /// <summary>
        /// What the column is for, in a line, so the prompt can say what it is
        /// asking for rather than only naming it. Empty for a custom attribute,
        /// whose meaning is the user's to explain in the run instruction.
        /// </summary>
        public string Description { get; init; } = "";

        public bool IsCustom { get; init; }

        /// <summary>crm_custom_fields.id, for a custom attribute.</summary>
        public int CustomFieldId { get; init; }

        /// <summary>text | number | date | dropdown … — a custom attribute's type.</summary>
        public string? FieldType { get; init; }

        /// <summary>
        /// A dropdown's permitted values. Sent to the model so it answers with
        /// one of them instead of inventing a sixth option the column cannot
        /// hold.
        /// </summary>
        public IReadOnlyList<string> Options { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// The columns Customisable Data Enhancement may be asked to fill, and how
    /// each one is read and written.
    ///
    /// The other four checks have a fixed subject, so the fields they may write
    /// are a compiled-in list (<see cref="ValidationSuggestionFields"/>). This
    /// one is handed its subject at run time, so the question "may this check
    /// write this column?" is answered twice: here, for whether the column is
    /// writable at all, and by <see cref="EnhancementFieldSet"/>, for whether
    /// it was one of the columns the user actually picked for that run. A model
    /// naming anything else is dropped at parse time and refused again at the
    /// point of writing.
    ///
    /// What is deliberately absent is as much the point as what is here. The
    /// krafted subject and body, the LinkedIn notes and the web research blob
    /// are all generated content rather than CRM data: overwriting one of those
    /// from a research prompt would destroy work rather than improve a record.
    /// </summary>
    public static class DataEnhancementFields
    {
        public const string CustomPrefix = "custom:";

        /// <summary>The key a custom attribute is addressed by.</summary>
        public static string CustomKey(int customFieldId) => CustomPrefix + customFieldId;

        /// <summary>Reads a <c>custom:17</c> key back to its field id.</summary>
        public static bool TryParseCustomKey(string? key, out int customFieldId)
        {
            customFieldId = 0;

            if (string.IsNullOrWhiteSpace(key)) return false;

            var trimmed = key.Trim();

            return trimmed.StartsWith(CustomPrefix, StringComparison.OrdinalIgnoreCase) &&
                   int.TryParse(trimmed[CustomPrefix.Length..], out customFieldId) &&
                   customFieldId > 0;
        }

        // ------------------------------------------------------------ system

        /// <summary>
        /// <paramref name="RowKey"/> is the name this column arrives under on a
        /// contact row in the browser, which is not always the key.
        ///
        /// The grid is generated from whatever keys a row carries, and a row is
        /// the API's JSON: camel-cased by MVC, so <c>CompanyIndustry</c> reaches
        /// the browser as <c>companyIndustry</c> while <c>job_title</c> stays as
        /// it is. Accepting a correction patches the one row on screen rather
        /// than refetching the list, and a patch written under the wrong name
        /// is a write that reports success and changes nothing visible - which
        /// is exactly what happened to the four company columns.
        /// </summary>
        private sealed record SystemColumn(
            string Key,
            string RowKey,
            string Label,
            string Description,
            Func<Contact, string?> Read,
            Action<Contact, string?> Write);

        /// <summary>
        /// The contact columns, in the order the picker lists them: who the
        /// person is, how to reach them, then what is known about the employer.
        /// </summary>
        private static readonly SystemColumn[] Columns =
        {
            new(ValidationSuggestionFields.FullName, "full_name", "Name",
                "The person's full name, as it should be written to them.",
                c => c.full_name,
                (c, v) =>
                {
                    c.full_name = v;

                    // Split exactly as Crm/update-contact splits it: the grid,
                    // the profile header and the merge fields each read a
                    // different one of the three columns, so writing only
                    // full_name fixes the name in one place and leaves it
                    // broken in two.
                    var parts = (v ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    c.first_name = parts.FirstOrDefault();
                    c.last_name = parts.Length > 1 ? string.Join(" ", parts.Skip(1)) : "";
                }),

            new("first_name", "first_name", "First name",
                "The person's given name on its own.",
                c => c.first_name,
                (c, v) => c.first_name = v),

            new("last_name", "last_name", "Last name",
                "The person's family name on its own.",
                c => c.last_name,
                (c, v) => c.last_name = v),

            new(ValidationSuggestionFields.JobTitle, "job_title", "Job title",
                "The role the person holds at the company named on the record.",
                c => c.job_title,
                (c, v) => c.job_title = v),

            new(ValidationSuggestionFields.CompanyName, "company_name", "Company",
                "The employer's name as the company itself writes it.",
                c => c.company_name,
                (c, v) => c.company_name = v),

            new(ValidationSuggestionFields.Email, "email", "Email",
                "The person's work email address. Only propose one you have direct evidence for — never a pattern guessed from the domain.",
                c => c.email,
                (c, v) => c.email = string.IsNullOrWhiteSpace(v) ? null : v),

            new(ValidationSuggestionFields.Website, "website", "Website",
                "The employer's own website.",
                c => c.website,
                (c, v) => c.website = v),

            new(ValidationSuggestionFields.LinkedInUrl, "linkedin_url", "LinkedIn URL",
                "The person's own LinkedIn profile URL.",
                c => c.linkedin_url,
                (c, v) => c.linkedin_url = v),

            new(ValidationSuggestionFields.Location, "country_or_address", "Location",
                "Where the person is based — the country, or the office address.",
                c => c.country_or_address,
                (c, v) => c.country_or_address = v),

            new("company_telephone", "companyTelephone", "Company telephone",
                "The employer's published switchboard or main contact number.",
                c => c.CompanyTelephone,
                (c, v) => c.CompanyTelephone = v),

            new("company_industry", "companyIndustry", "Company industry",
                "What the employer actually does, in a few words.",
                c => c.CompanyIndustry,
                (c, v) => c.CompanyIndustry = v),

            new("company_employee_count", "companyEmployeeCount", "Company employee count",
                "Roughly how many people the employer has, as a number or a band.",
                c => c.CompanyEmployeeCount,
                (c, v) => c.CompanyEmployeeCount = v),

            new("company_linkedin_url", "companyLinkedInURL", "Company LinkedIn URL",
                "The employer's LinkedIn company page.",
                c => c.CompanyLinkedInURL,
                (c, v) => c.CompanyLinkedInURL = v)
        };

        private static readonly Dictionary<string, SystemColumn> ByKey =
            Columns.ToDictionary(column => column.Key, StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Spellings a model, or an older saved run, reasonably reaches for.
        /// The shared ones are already handled by
        /// <see cref="ValidationSuggestionFields.Normalize"/>; these are the
        /// columns only this check can write.
        /// </summary>
        private static readonly Dictionary<string, string> Aliases =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["firstname"] = "first_name",
                ["first name"] = "first_name",
                ["given_name"] = "first_name",
                ["lastname"] = "last_name",
                ["last name"] = "last_name",
                ["surname"] = "last_name",
                ["family_name"] = "last_name",
                ["companytelephone"] = "company_telephone",
                ["company telephone"] = "company_telephone",
                ["company_phone"] = "company_telephone",
                ["phone"] = "company_telephone",
                ["telephone"] = "company_telephone",
                ["companyindustry"] = "company_industry",
                ["company industry"] = "company_industry",
                ["industry"] = "company_industry",
                ["companyemployeecount"] = "company_employee_count",
                ["company employee count"] = "company_employee_count",
                ["employee_count"] = "company_employee_count",
                ["employees"] = "company_employee_count",
                ["headcount"] = "company_employee_count",
                ["company_size"] = "company_employee_count",
                ["companylinkedinurl"] = "company_linkedin_url",
                ["company linkedin url"] = "company_linkedin_url",
                ["company_linkedin"] = "company_linkedin_url",
                ["company_page"] = "company_linkedin_url"
            };

        /// <summary>The canonical key for a contact column, or null if it is not one.</summary>
        public static string? NormalizeSystem(string? field)
        {
            if (string.IsNullOrWhiteSpace(field)) return null;

            var trimmed = field.Trim();

            if (ByKey.TryGetValue(trimmed, out var column)) return column.Key;

            if (Aliases.TryGetValue(trimmed, out var alias)) return alias;

            // The seven fields the other checks correct share their spellings
            // and their aliases with this one; "title" and "company" should not
            // mean one thing here and another there.
            var shared = ValidationSuggestionFields.Normalize(trimmed);

            return shared != null && ByKey.ContainsKey(shared) ? shared : null;
        }

        /// <summary>Every contact column the check may be pointed at.</summary>
        public static IReadOnlyList<EnhancementField> SystemFields { get; } =
            Columns
                .Select(column => new EnhancementField
                {
                    Key = column.Key,
                    Label = column.Label,
                    Description = column.Description
                })
                .ToList();

        public static string SystemLabel(string key) =>
            ByKey.TryGetValue(key, out var column) ? column.Label : key;

        /// <summary>
        /// The name this column arrives under on a contact row in the browser,
        /// so an accepted value can be patched into the row on screen. Falls
        /// back to the key, which is already the row name for every column
        /// whose key is the database column name.
        /// </summary>
        public static string RowKeyFor(string? key)
        {
            var normalised = NormalizeSystem(key);

            return normalised != null && ByKey.TryGetValue(normalised, out var column)
                ? column.RowKey
                : key ?? "";
        }

        /// <summary>The value the contact holds now, for the "current" side of the diff.</summary>
        public static string? ReadSystem(Contact contact, string key) =>
            ByKey.TryGetValue(key, out var column) ? column.Read(contact) : null;

        /// <summary>
        /// Writes an accepted value onto the contact. Returns false for a key
        /// that is not a contact column, so the caller refuses rather than
        /// reporting success on a write that never happened.
        /// </summary>
        public static bool ApplySystem(Contact contact, string key, string? value)
        {
            if (!ByKey.TryGetValue(key, out var column)) return false;

            var trimmed = value?.Trim();

            // An empty replacement is never applied. Every suggestion this
            // check produces is an addition or a correction; "clear this
            // column" is not something a research prompt should be able to ask
            // for with one click.
            if (string.IsNullOrWhiteSpace(trimmed)) return false;

            column.Write(contact, trimmed);
            return true;
        }
    }

    /// <summary>
    /// The columns one enhancement run was pointed at, resolved against the
    /// client's own custom attributes.
    ///
    /// Built once per run and once per Accept, and it is what turns "the model
    /// said to write Industry" into "write custom attribute 17, which this
    /// client owns and this run asked about". Without it the field name in a
    /// suggestion would be a claim with nothing behind it — which is the whole
    /// reason the other four checks get a compiled-in list instead.
    /// </summary>
    public sealed class EnhancementFieldSet
    {
        private readonly Dictionary<string, EnhancementField> _byKey =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Every spelling that resolves to a key, including labels.</summary>
        private readonly Dictionary<string, string> _lookup =
            new(StringComparer.OrdinalIgnoreCase);

        private EnhancementFieldSet() { }

        public IReadOnlyCollection<EnhancementField> Fields => _byKey.Values;

        public int Count => _byKey.Count;

        public bool IsEmpty => _byKey.Count == 0;

        /// <summary>
        /// Resolves the field name on a suggestion to one of this run's
        /// columns, or null when it names something else.
        ///
        /// Lenient about spelling for the same reason the score parsing is: a
        /// model that answered about the right column under its on-screen label
        /// has done the work, and throwing that away costs a re-run for
        /// nothing. It is not lenient about *which* columns — anything outside
        /// this set resolves to null however it is spelled.
        /// </summary>
        public string? Resolve(string? field)
        {
            if (string.IsNullOrWhiteSpace(field)) return null;

            var trimmed = field.Trim();

            if (_byKey.ContainsKey(trimmed)) return _byKey[trimmed].Key;

            if (_lookup.TryGetValue(trimmed, out var key)) return key;

            var system = DataEnhancementFields.NormalizeSystem(trimmed);

            return system != null && _byKey.ContainsKey(system) ? system : null;
        }

        public EnhancementField? Get(string? key)
        {
            var resolved = Resolve(key);

            return resolved != null && _byKey.TryGetValue(resolved, out var field) ? field : null;
        }

        public string LabelFor(string? key) => Get(key)?.Label ?? key ?? "";

        /// <summary>
        /// Builds the set from the keys a run asked for and the client's custom
        /// attributes.
        ///
        /// Unknown keys are dropped rather than refused: a run saved before an
        /// attribute was deleted should still be able to show, and apply, the
        /// suggestions it made for the columns that are still there.
        /// </summary>
        public static EnhancementFieldSet Build(
            IEnumerable<string>? keys,
            IEnumerable<CrmCustomField>? customFields)
        {
            var set = new EnhancementFieldSet();

            var customById = (customFields ?? Enumerable.Empty<CrmCustomField>())
                .GroupBy(field => field.id)
                .ToDictionary(group => group.Key, group => group.First());

            foreach (var raw in keys ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;

                if (DataEnhancementFields.TryParseCustomKey(raw, out var customFieldId))
                {
                    if (!customById.TryGetValue(customFieldId, out var custom)) continue;

                    set.Add(new EnhancementField
                    {
                        Key = DataEnhancementFields.CustomKey(custom.id),
                        Label = string.IsNullOrWhiteSpace(custom.field_name)
                            ? custom.field_key ?? $"Attribute {custom.id}"
                            : custom.field_name,
                        IsCustom = true,
                        CustomFieldId = custom.id,
                        FieldType = custom.field_type,
                        Options = ReadOptions(custom.options_json)
                    });

                    // The stored key as well as the display name: a model shown
                    // "Industry" may still answer with "industry_code".
                    if (!string.IsNullOrWhiteSpace(custom.field_key))
                        set._lookup.TryAdd(custom.field_key.Trim(), DataEnhancementFields.CustomKey(custom.id));

                    continue;
                }

                var system = DataEnhancementFields.NormalizeSystem(raw);
                if (system == null) continue;

                set.Add(new EnhancementField
                {
                    Key = system,
                    Label = DataEnhancementFields.SystemLabel(system),
                    Description = DataEnhancementFields.SystemFields
                        .First(field => field.Key == system).Description
                });
            }

            return set;
        }

        private void Add(EnhancementField field)
        {
            if (_byKey.ContainsKey(field.Key)) return;

            _byKey[field.Key] = field;

            // The label is how the model will most often name the column, since
            // that is what the prompt shows it.
            if (!string.IsNullOrWhiteSpace(field.Label))
                _lookup.TryAdd(field.Label.Trim(), field.Key);
        }

        /// <summary>
        /// A dropdown's options. The column stores them as a JSON array; a
        /// malformed blob yields none rather than failing the run.
        /// </summary>
        private static IReadOnlyList<string> ReadOptions(string? optionsJson)
        {
            if (string.IsNullOrWhiteSpace(optionsJson)) return Array.Empty<string>();

            try
            {
                return Newtonsoft.Json.JsonConvert
                           .DeserializeObject<List<string>>(optionsJson)
                           ?.Where(option => !string.IsNullOrWhiteSpace(option))
                           .ToList()
                       ?? (IReadOnlyList<string>)Array.Empty<string>();
            }
            catch (Newtonsoft.Json.JsonException)
            {
                return Array.Empty<string>();
            }
        }
    }
}
