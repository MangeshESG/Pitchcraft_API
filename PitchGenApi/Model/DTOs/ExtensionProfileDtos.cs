namespace PitchGenApi.Model.DTOs
{
    /// <summary>
    /// The contact fields the browser extension can read from LinkedIn and write
    /// into Pitchkraft. Every property is nullable so a save request can carry
    /// only the fields the user actually ticked; null means "leave unchanged".
    /// </summary>
    public class ExtensionContactFieldsDto
    {
        public string? FullName { get; set; }
        public string? JobTitle { get; set; }
        public string? Location { get; set; }
        public string? Email { get; set; }
        public string? LinkedInUrl { get; set; }
        public string? CompanyName { get; set; }
        public string? Website { get; set; }
        public string? CompanyIndustry { get; set; }
        public string? CompanyEmployeeCount { get; set; }
        public string? CompanyLinkedInUrl { get; set; }
    }

    /// <summary>
    /// Asked once when the extension panel opens: does this LinkedIn URL already
    /// exist in any of the client's lists, and which lists can it be saved into?
    /// </summary>
    public class ExtensionProfileContextRequestDto
    {
        public int ClientId { get; set; }
        public string? LinkedInUrl { get; set; }
    }

    /// <summary>
    /// Creates the contact when ContactId is absent, otherwise updates the named
    /// contact with just the supplied fields.
    /// </summary>
    public class ExtensionSaveProfileRequestDto
    {
        public int ClientId { get; set; }
        public int? ContactId { get; set; }
        public int? DataFileId { get; set; }
        public ExtensionContactFieldsDto? Fields { get; set; }
    }

    /// <summary>
    /// Sends the scraped profile text to the LLM and stores the returned summary
    /// in the contact's linkedIninformation system field.
    /// </summary>
    public class ExtensionProfileSummaryRequestDto
    {
        public int ClientId { get; set; }
        public int? ContactId { get; set; }
        public string? LinkedInUrl { get; set; }
        public string? ContactName { get; set; }
        public string? CompanyName { get; set; }

        /// <summary>Visible text scraped from the LinkedIn profile page.</summary>
        public string? ProfileText { get; set; }

        /// <summary>False previews the summary without writing it to the contact.</summary>
        public bool Save { get; set; } = true;
    }
}
