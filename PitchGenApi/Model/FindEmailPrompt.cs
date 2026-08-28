namespace PitchGenApi.Model
{
    /// <summary>
    /// Placeholder substitution for the email research instruction behind
    /// POST /api/Extension/find-email-AI and the extension's unlock button.
    ///
    /// The instruction itself is not in code. It lives in app_prompt_settings
    /// under <see cref="PromptKeys.FindEmail"/>, is edited from
    /// Settings &gt; Admin &gt; Prompts, and is read through
    /// IPromptSettingsService; there is no compiled-in copy to fall back on, so
    /// an unsaved prompt means no search runs. Either way the request payload
    /// carries only the contact details, never the instruction.
    /// </summary>
    public static class FindEmailPrompt
    {
        /// <summary>Shown in place of any input the caller did not send.</summary>
        public const string MissingValue = "Not provided";

        /// <summary>
        /// Every placeholder the instruction supports. The value is whatever the
        /// caller sent for that field, or <see cref="MissingValue"/>.
        /// The "{job_ title}" form is accepted too because that spelling is in
        /// circulation in existing prompt copies.
        /// </summary>
        private static readonly string[] JobTitleTokens = { "{job_title}", "{job_ title}" };

        /// <summary>
        /// Fills the placeholders in <paramref name="template"/> - the stored
        /// instruction - with the contact details. Any field the caller left
        /// empty becomes <see cref="MissingValue"/>, so a request carrying only
        /// a name and a domain still produces a usable prompt. A template that
        /// dropped a placeholder simply has nothing substituted for it.
        /// </summary>
        public static string Build(
            string template,
            string? fullName,
            string? jobTitle,
            string? company,
            string? location,
            string? profileUrl,
            string? companyUrl)
        {
            var prompt = template ?? "";

            prompt = prompt.Replace("{full_name}", Value(fullName));

            foreach (var token in JobTitleTokens)
            {
                prompt = prompt.Replace(token, Value(jobTitle));
            }

            // "{company_url}" survives the "{company}" pass because the literal
            // token differs - no ordering trap here.
            prompt = prompt.Replace("{company}", Value(company));
            prompt = prompt.Replace("{location}", Value(location));
            prompt = prompt.Replace("{profile_url}", Value(profileUrl));
            prompt = prompt.Replace("{company_url}", Value(companyUrl));

            return prompt;
        }

        private static string Value(string? value) =>
            string.IsNullOrWhiteSpace(value) ? MissingValue : value.Trim();
    }
}
