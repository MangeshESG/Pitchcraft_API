namespace PitchGenApi.Model.DTOs
{
    /// <summary>
    /// Input for POST /api/Extension/find-email-AI. Every identifying field is
    /// optional — whatever is missing is sent to the model as "Not provided" —
    /// but at least one of them has to be present for the search to mean
    /// anything.
    /// </summary>
    public sealed class FindEmailAiRequestDto
    {
        /// <summary>
        /// Client to bill — one credit is deducted per search. Optional only
        /// when the authenticated token carries a UserId claim, which is used
        /// instead; if both are sent they must match.
        /// </summary>
        public int ClientId { get; set; }

        public string? FullName { get; set; }
        public string? JobTitle { get; set; }
        public string? Company { get; set; }
        public string? Location { get; set; }

        /// <summary>LinkedIn or any other public profile URL.</summary>
        public string? ProfileUrl { get; set; }

        /// <summary>Company website or bare domain.</summary>
        public string? CompanyUrl { get; set; }

        // The research instruction is deliberately NOT part of this payload —
        // it comes from the admin-editable prompt (app_prompt_settings, key
        // find_email) so callers cannot change how the search is performed.
    }
}
