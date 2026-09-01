namespace PitchGenApi.Model
{
    /// <summary>
    /// The instructions an admin can edit from the UI, and everything the page
    /// needs to describe one: its label, what it drives, and the placeholders
    /// the text may use. The text itself is only ever in app_prompt_settings -
    /// nothing is compiled in, so an unsaved prompt is simply empty.
    ///
    /// Adding a prompt here is all it takes for it to appear on the admin page
    /// — the controller and the service both iterate <see cref="All"/>.
    /// </summary>
    public static class PromptKeys
    {
        /// <summary>Email research behind the extension's unlock button.</summary>
        public const string FindEmail = "find_email";

        /// <summary>Audience Assurance: scores contacts against a saved targeting brief.</summary>
        public const string ContactFit = ValidationCheckTypes.ContactFit;

        /// <summary>Audience Assurance: checks the supplied record for structural problems.</summary>
        public const string DataIntegrity = ValidationCheckTypes.DataIntegrity;

        /// <summary>Audience Assurance: checks whether the contact is still current.</summary>
        public const string LiveContact = ValidationCheckTypes.LiveContact;

        public static readonly string[] All =
        {
            FindEmail,
            ContactFit,
            DataIntegrity,
            LiveContact
        };

        public static bool IsKnown(string? key) =>
            !string.IsNullOrWhiteSpace(key) &&
            All.Any(known => string.Equals(known, key, StringComparison.OrdinalIgnoreCase));

        /// <summary>The canonical spelling of a key the caller may have cased differently.</summary>
        public static string Normalize(string key) =>
            All.FirstOrDefault(known => string.Equals(known, key, StringComparison.OrdinalIgnoreCase))
            ?? key;

        public static (string Label, string Description) Describe(string key) => Normalize(key) switch
        {
            FindEmail => (
                "Find email (extension unlock)",
                "The research instruction sent to the model when the browser extension unlocks a contact's email address. It also asks for the company website, industry and size, so keep the JSON output block intact."),
            ContactFit => (
                "Contact fit (Audience Assurance)",
                "Scores selected contacts against the saved targeting brief. {brief} is replaced with the brief the user picked and {company_intelligence} with what we already know about those companies — leaving that placeholder out means every company gets researched again, which is what web search costs money for. Keep the JSON output block intact."),
            DataIntegrity => (
                "Data integrity (Audience Assurance)",
                "Checks the supplied record for missing fields, generic or malformed emails, contaminated names and titles, domain mismatches and duplicates. Runs with web search off, so it stays nearly free. The rule that comments contain problems only is what keeps the column readable — don't soften it. Keep the JSON output block intact."),
            LiveContact => (
                "Live contact (Audience Assurance)",
                "Checks against current public evidence whether the person is still at that company in that role. This is the most search-heavy check, so the instruction to reuse evidence across contacts at the same employer is doing real work. Keep the JSON output block intact."),
            _ => (key, "")
        };

        /// <summary>
        /// Placeholders the text may contain. They are replaced with the
        /// contact's details at send time; anything the caller did not send
        /// becomes <see cref="FindEmailPrompt.MissingValue"/>.
        /// </summary>
        public static string[] Placeholders(string key) => Normalize(key) switch
        {
            FindEmail => new[]
            {
                "{full_name}",
                "{job_title}",
                "{company}",
                "{location}",
                "{profile_url}",
                "{company_url}"
            },
            ContactFit => new[]
            {
                "{brief}",
                "{company_intelligence}",
                "{contacts_json}"
            },
            DataIntegrity => new[]
            {
                "{duplicate_flags}",
                "{contacts_json}"
            },
            LiveContact => new[]
            {
                "{contacts_json}"
            },
            _ => Array.Empty<string>()
        };
    }
}
