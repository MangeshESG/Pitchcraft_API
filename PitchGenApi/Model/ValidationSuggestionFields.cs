using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using PitchGenApi.Model.DTOs;
using PitchGenApi.Models;

namespace PitchGenApi.Model
{
    /// <summary>
    /// The contact fields a Data Integrity suggestion is allowed to correct.
    ///
    /// The model writes a field name into its JSON, and that name ends up
    /// naming a column we are about to overwrite — so the set of writable
    /// fields is decided here rather than by whatever the model happened to
    /// say. A suggestion naming anything else is dropped at parse time and
    /// never reaches the grid, which is what stops a prompt edit, or a model
    /// improvising, from turning into a write to an unrelated column.
    ///
    /// Adding a field here is all it takes for the check to be able to suggest
    /// a correction to it: the parser, the Accept endpoint and the UI label all
    /// read this list.
    /// </summary>
    public static class ValidationSuggestionFields
    {
        public const string FullName = "full_name";
        public const string JobTitle = "job_title";
        public const string CompanyName = "company_name";
        public const string Email = "email";
        public const string Website = "website";
        public const string Location = "country_or_address";
        public const string LinkedInUrl = "linkedin_url";

        public static readonly IReadOnlyList<string> All = new[]
        {
            FullName,
            JobTitle,
            CompanyName,
            Email,
            Website,
            Location,
            LinkedInUrl
        };

        /// <summary>
        /// Spellings a model reasonably reaches for, mapped onto the canonical
        /// key. A suggestion that names the right field in the wrong dialect
        /// has done the work; throwing it away would cost a re-run for nothing.
        /// </summary>
        private static readonly Dictionary<string, string> Aliases =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["name"] = FullName,
                ["fullname"] = FullName,
                ["full name"] = FullName,
                ["contact_name"] = FullName,
                ["person_name"] = FullName,
                ["jobtitle"] = JobTitle,
                ["job title"] = JobTitle,
                ["title"] = JobTitle,
                ["role"] = JobTitle,
                ["company"] = CompanyName,
                ["companyname"] = CompanyName,
                ["company name"] = CompanyName,
                ["email_address"] = Email,
                ["emailaddress"] = Email,
                ["company_website"] = Website,
                ["companywebsite"] = Website,
                ["url"] = Website,
                ["domain"] = Website,
                ["location"] = Location,
                ["address"] = Location,
                ["country"] = Location,
                ["country_or_address"] = Location,
                ["linkedin"] = LinkedInUrl,
                ["linkedinurl"] = LinkedInUrl,
                ["linkedin_profile"] = LinkedInUrl
            };

        /// <summary>The canonical key, or null when the field is not writable.</summary>
        public static string? Normalize(string? field)
        {
            if (string.IsNullOrWhiteSpace(field)) return null;

            var trimmed = field.Trim();

            var known = All.FirstOrDefault(
                key => string.Equals(key, trimmed, StringComparison.OrdinalIgnoreCase));

            if (known != null) return known;

            return Aliases.TryGetValue(trimmed, out var alias) ? alias : null;
        }

        public static bool IsKnown(string? field) => Normalize(field) != null;

        /// <summary>What the field is called in the UI.</summary>
        public static string Label(string field) => Normalize(field) switch
        {
            FullName => "Name",
            JobTitle => "Job title",
            CompanyName => "Company",
            Email => "Email",
            Website => "Website",
            Location => "Location",
            LinkedInUrl => "LinkedIn URL",
            _ => field
        };

        /// <summary>The value the contact holds now, for the "current" side of the diff.</summary>
        public static string? Read(Contact contact, string field) => Normalize(field) switch
        {
            FullName => contact.full_name,
            JobTitle => contact.job_title,
            CompanyName => contact.company_name,
            Email => contact.email,
            Website => contact.website,
            Location => contact.country_or_address,
            LinkedInUrl => contact.linkedin_url,
            _ => null
        };

        /// <summary>
        /// Writes the accepted value onto the contact. Returns false for a
        /// field outside the list, so the caller can refuse rather than
        /// silently report success on a write that never happened.
        ///
        /// The name is split into first and last exactly as
        /// <c>Crm/update-contact</c> splits it, because the grid, the profile
        /// header and the merge fields each read a different one of the three
        /// columns — accepting "Mike Thompson" and leaving last_name at "T."
        /// would fix the name in one place and leave it broken in two.
        /// </summary>
        public static bool Apply(Contact contact, string field, string? value)
        {
            var trimmed = value?.Trim();

            switch (Normalize(field))
            {
                case FullName:
                    if (string.IsNullOrWhiteSpace(trimmed)) return false;

                    contact.full_name = trimmed;

                    var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    contact.first_name = parts.FirstOrDefault();
                    contact.last_name = parts.Length > 1 ? string.Join(" ", parts.Skip(1)) : "";
                    return true;

                case JobTitle:
                    contact.job_title = trimmed;
                    return true;

                case CompanyName:
                    contact.company_name = trimmed;
                    return true;

                case Email:
                    // An empty email is null rather than "", matching what
                    // update-contact stores, so the two paths cannot disagree
                    // about what "no email" looks like.
                    contact.email = string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
                    return true;

                case Website:
                    contact.website = trimmed;
                    return true;

                case Location:
                    contact.country_or_address = trimmed;
                    return true;

                case LinkedInUrl:
                    contact.linkedin_url = trimmed;
                    return true;

                default:
                    return false;
            }
        }
    }

    /// <summary>Where one suggestion has got to.</summary>
    public static class ValidationSuggestionStatuses
    {
        public const string Pending = "pending";
        public const string Accepted = "accepted";
        public const string Dismissed = "dismissed";
    }

    /// <summary>
    /// Reads and writes the stored suggestions blob.
    /// </summary>
    /// <remarks>
    /// The casing is pinned rather than left to Newtonsoft's default, because
    /// this blob is read twice in two different ways: the results endpoint
    /// sends it through MVC, which camel-cases it, and the contact list
    /// endpoints hand the stored string straight to the browser. Newtonsoft's
    /// default would write Pascal case into the column, and the grid would then
    /// get "Suggested" where the profile panel got "suggested" — the same data
    /// arriving under two names, with whichever half was written second
    /// appearing to be missing its fields.
    ///
    /// Reading is case-insensitive either way, so this is only about what goes
    /// into the column.
    /// </remarks>
    public static class ValidationSuggestionJson
    {
        private static readonly JsonSerializerSettings Settings = new()
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver()
        };

        public static string Serialize(IEnumerable<ValidationSuggestionDto> suggestions) =>
            JsonConvert.SerializeObject(suggestions, Settings);

        /// <summary>
        /// A malformed blob yields none rather than taking the row's scores
        /// down with it.
        /// </summary>
        public static List<ValidationSuggestionDto> Deserialize(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new List<ValidationSuggestionDto>();

            try
            {
                return JsonConvert.DeserializeObject<List<ValidationSuggestionDto>>(json)
                       ?? new List<ValidationSuggestionDto>();
            }
            catch (JsonException)
            {
                return new List<ValidationSuggestionDto>();
            }
        }
    }
}
