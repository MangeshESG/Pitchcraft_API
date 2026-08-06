namespace PitchGenApi.Model
{
    /// <summary>
    /// The AI purposes an admin can point at a model. The keys are stored in
    /// ai_model_settings.purpose_key, so they are part of the API contract with
    /// the admin UI — don't rename them without a data migration.
    /// </summary>
    public static class AiModelPurposes
    {
        public const string WebSearch = "web_search";
        public const string BlueprintGeneration = "blueprint_generation";
        public const string EmailGeneration = "email_generation";
        public const string ContactQA = "contact_qa";

        public static readonly IReadOnlyList<string> All = new[]
        {
            WebSearch,
            BlueprintGeneration,
            EmailGeneration,
            ContactQA
        };

        public static bool IsKnown(string? purposeKey) =>
            !string.IsNullOrWhiteSpace(purposeKey) &&
            All.Contains(purposeKey, StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Display metadata for the admin page, so the labels live with the keys
        /// instead of being duplicated in the frontend.
        /// </summary>
        public static (string Label, string Description) Describe(string purposeKey) =>
            purposeKey switch
            {
                WebSearch => (
                    "Web search",
                    "Runs the research step that fills {web_searched_data} and the contact Insights panel."),
                BlueprintGeneration => (
                    "Blueprint generation",
                    "Powers the blueprint builder conversation and example-output generation."),
                EmailGeneration => (
                    "Email generation",
                    "Writes email bodies and subject lines when a contact is krafted."),
                ContactQA => (
                    "Contact Q&A",
                    "Answers questions asked on a contact profile from CRM context."),
                _ => (purposeKey, "")
            };
    }
}
