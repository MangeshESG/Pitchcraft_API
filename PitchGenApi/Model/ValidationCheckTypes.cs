namespace PitchGenApi.Model
{
    /// <summary>
    /// The four Audience Assurance checks a user can run over selected contacts.
    ///
    /// The keys are stored in contact_validation_jobs.check_type and are the
    /// same strings used as the <see cref="AiModelPurposes"/> and
    /// <see cref="PromptKeys"/> keys for the three AI-backed checks, so an admin
    /// picking a model for "Contact fit" is picking the model this check runs
    /// on. Don't rename them without a data migration.
    ///
    /// The checks are deliberately independent: a contact can have excellent
    /// data integrity and still fail contact fit, so no score is ever derived
    /// from another.
    /// </summary>
    public static class ValidationCheckTypes
    {
        /// <summary>Does this company + job title belong in the target audience?</summary>
        public const string ContactFit = "contact_fit";

        /// <summary>Is the supplied record complete, clean and logically consistent?</summary>
        public const string DataIntegrity = "data_integrity";

        /// <summary>Is the person still at that company in that role today?</summary>
        public const string LiveContact = "live_contact";

        /// <summary>Is the address real and deliverable? Runs on Prospeo/Hunter, not a model.</summary>
        public const string EmailVerification = "email_verification";

        public static readonly IReadOnlyList<string> All = new[]
        {
            ContactFit,
            DataIntegrity,
            LiveContact,
            EmailVerification
        };

        public static bool IsKnown(string? checkType) =>
            !string.IsNullOrWhiteSpace(checkType) &&
            All.Contains(checkType, StringComparer.OrdinalIgnoreCase);

        /// <summary>The canonical spelling of a key the caller may have cased differently.</summary>
        public static string Normalize(string checkType) =>
            All.FirstOrDefault(known => string.Equals(known, checkType, StringComparison.OrdinalIgnoreCase))
            ?? checkType;

        /// <summary>
        /// Whether the check needs a saved Contact Fit brief. Only contact fit
        /// does — the others judge the record itself, not the audience.
        /// </summary>
        public static bool RequiresBrief(string checkType) =>
            Normalize(checkType) == ContactFit;

        /// <summary>
        /// Whether the check runs on a language model at all. Email
        /// verification goes to Prospeo and Hunter instead, so it has no
        /// prompt, no model setting and no token cost.
        /// </summary>
        public static bool UsesModel(string checkType) =>
            Normalize(checkType) != EmailVerification;

        /// <summary>
        /// Whether the check needs live web evidence.
        ///
        /// Data integrity is pure logic over the supplied record, so it must
        /// never search — that is what makes it nearly free, and searching
        /// would also blur the line into the live contact check.
        /// </summary>
        public static bool UsesWebSearch(string checkType) => Normalize(checkType) switch
        {
            ContactFit => true,
            LiveContact => true,
            _ => false
        };

        public static (string Label, string Description) Describe(string checkType) => Normalize(checkType) switch
        {
            ContactFit => (
                "Contact fit",
                "Scores each contact against a saved targeting brief: is this company, and this job title, someone we want in the audience?"),
            DataIntegrity => (
                "Data integrity",
                "Checks the record itself for missing fields, generic or malformed emails, contaminated names and titles, website/email domain mismatches and duplicates. Never uses web search."),
            LiveContact => (
                "Live contact",
                "Checks against current public evidence whether the person is still at that company in that role."),
            EmailVerification => (
                "Email discovery and verification",
                "Confirms the address through Prospeo, falling back to Hunter. Runs no language model."),
            _ => (checkType, "")
        };
    }

    /// <summary>Lifecycle of one validation run.</summary>
    public static class ValidationJobStatuses
    {
        public const string Queued = "queued";
        public const string Running = "running";
        public const string Completed = "completed";

        /// <summary>Finished, but some contacts came back with no result.</summary>
        public const string Partial = "partial";

        public const string Failed = "failed";
    }

    /// <summary>Per-contact outcome inside a run, used for refunds and retries.</summary>
    public static class ValidationItemStatuses
    {
        public const string Pending = "pending";
        public const string Completed = "completed";
        public const string Failed = "failed";
    }
}
