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

        /// <summary>
        /// The one-off emails written from a conversation rather than from a
        /// campaign run: an inbox reply or forward, and Compose / the Emails
        /// timeline on a contact profile. Same endpoint as a kraft, but the
        /// job is different — it answers a message that is already on screen,
        /// usually short, and is written while someone waits for it — so it
        /// gets its own model instead of sharing the campaign writer's.
        /// </summary>
        public const string TransactionalEmail = "transactional_email";

        /// <summary>
        /// LinkedIn message generation. Shared the email writer's model until
        /// it got this key; a LinkedIn note has its own length and register, so
        /// it is worth being able to point somewhere cheaper or different.
        /// </summary>
        public const string LinkedInMessage = "linkedin_message";
        public const string ContactQA = "contact_qa";
        public const string FindEmail = "find_email";
        public const string ProfileSummary = "profile_summary";

        // The three AI-backed Audience Assurance checks. The keys are shared
        // with ValidationCheckTypes and PromptKeys, so picking a model here is
        // picking the model that check runs on.
        public const string ContactFit = ValidationCheckTypes.ContactFit;
        public const string DataIntegrity = ValidationCheckTypes.DataIntegrity;
        public const string LiveContact = ValidationCheckTypes.LiveContact;

        public static readonly IReadOnlyList<string> All = new[]
        {
            WebSearch,
            BlueprintGeneration,
            EmailGeneration,
            TransactionalEmail,
            LinkedInMessage,
            ContactQA,
            FindEmail,
            ProfileSummary,
            ContactFit,
            DataIntegrity,
            LiveContact
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
                    "Kraft email generation",
                    "Writes email bodies and subject lines when a contact is krafted from a campaign."),
                TransactionalEmail => (
                    "Transactional email generation",
                    "Writes inbox replies and forwards, and the Compose / Emails drafts on a contact profile."),
                LinkedInMessage => (
                    "LinkedIn message generation",
                    "Writes the LinkedIn messages krafted from a blueprint."),
                ContactQA => (
                    "Contact Q&A",
                    "Answers questions asked on a contact profile from CRM context."),
                FindEmail => (
                    "Find email (AI)",
                    "Researches a person's professional email address from public sources."),
                ProfileSummary => (
                    "Profile summary (extension)",
                    "Writes the professional summary from the LinkedIn profile the browser extension captured."),
                ContactFit => (
                    "Target Audience Match (Audience Assurance)",
                    "Scores selected contacts against a saved targeting brief. Needs web search, so pick a model that can reach the live web."),
                DataIntegrity => (
                    "Data Integrity Check (Audience Assurance)",
                    "Checks the supplied record for structural problems. Runs with web search off, so the cheapest capable model is the right choice here."),
                LiveContact => (
                    "Employment Match (Audience Assurance)",
                    "Checks whether the contact is still at that company in that role. The most search-heavy check — pick a model that can reach the live web."),
                _ => (purposeKey, "")
            };
    }
}
