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

        /// <summary>
        /// Fill in, or correct, whichever columns the user picked - including
        /// their own custom attributes - under an instruction they wrote.
        ///
        /// The odd one out of the five. The other four ask a fixed question of
        /// a fixed set of fields and answer it with a score; this one is handed
        /// its subject at run time, so the fields it may write are decided per
        /// run rather than by <see cref="ValidationSuggestionFields"/>, and
        /// everything it produces arrives as a suggestion the user accepts or
        /// dismisses. Nothing is ever written to a contact without that click.
        /// </summary>
        public const string DataEnhancement = "data_enhancement";

        public static readonly IReadOnlyList<string> All = new[]
        {
            ContactFit,
            DataIntegrity,
            LiveContact,
            EmailVerification,
            DataEnhancement
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
        /// Whether the check is told at run time which columns to work on.
        /// Only data enhancement is - the other four have a fixed subject, and
        /// a field list sent with one of them is ignored rather than obeyed.
        /// </summary>
        public static bool RequiresFields(string checkType) =>
            Normalize(checkType) == DataEnhancement;

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
            DataEnhancement => true,
            _ => false
        };

        /// <summary>
        /// Whether an answer that arrived without a single web search is
        /// worthless and has to be failed.
        ///
        /// Not the same question as <see cref="UsesWebSearch"/>, and data
        /// enhancement is why they had to come apart. It is offered the search
        /// tool because half of what it is asked to fill - an industry, a head
        /// count, a company phone number - cannot be known without it. But the
        /// other half can: splitting a full name, normalising a job title or
        /// deriving a website from an email address are all answerable from the
        /// record in front of it. A run over only those fields would search
        /// nothing and be right, and failing it would cost the user the whole
        /// batch for having asked an easy question.
        ///
        /// The two research checks keep the hard rule: their verdict means
        /// "current public evidence says so", and a verdict reached without
        /// looking is indistinguishable from one that was - which is exactly
        /// the failure the rule exists to catch.
        /// </summary>
        public static bool RequiresSearchEvidence(string checkType) => Normalize(checkType) switch
        {
            ContactFit => true,
            LiveContact => true,
            _ => false
        };

        public static (string Label, string Description) Describe(string checkType) => Normalize(checkType) switch
        {
            ContactFit => (
                "Target Audience Match",
                "Scores each contact against a saved targeting brief: is this company, and this job title, someone we want in the audience?"),
            DataIntegrity => (
                "Data Integrity Check",
                "Checks the record itself for missing fields, generic or malformed emails, contaminated names and titles, website/email domain mismatches and duplicates. Never uses web search."),
            LiveContact => (
                "Employment Match",
                "Checks against current public evidence whether the person is still at that company in that role."),
            EmailVerification => (
                "Email discovery and verification",
                "Finds and confirms the address through the same four stages as the extension: a recent unlock, then Prospeo, then an AI web search, then Hunter. Costs one credit per contact."),
            DataEnhancement => (
                "Customisable Data Enhancement",
                "Pick the columns you want filled or checked - including your own custom attributes - add an instruction in your own words, and the AI researches each contact and proposes a value for every column it can. Nothing is written until you accept it."),
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
