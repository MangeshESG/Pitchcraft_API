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

        public static readonly string[] All = { FindEmail };

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
            _ => Array.Empty<string>()
        };
    }
}
