namespace PitchGenApi.Model.DTOs
{
    // ------------------------------------------------------------ briefs

    public sealed class ContactFitBriefDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string BriefText { get; set; } = "";
        public bool IsDefault { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public string? UpdatedBy { get; set; }
    }

    public sealed class SaveContactFitBriefDto
    {
        /// <summary>Omitted or 0 creates a new brief.</summary>
        public int Id { get; set; }
        public int ClientId { get; set; }
        public string Name { get; set; } = "";
        public string BriefText { get; set; } = "";
        public bool IsDefault { get; set; }
        public string? UpdatedBy { get; set; }
    }

    // ------------------------------------------------------------ running

    public sealed class RunValidationRequestDto
    {
        public int ClientId { get; set; }

        /// <summary>One of <see cref="ValidationCheckTypes"/>.</summary>
        public string CheckType { get; set; } = "";

        public List<int> ContactIds { get; set; } = new();

        /// <summary>Required for contact fit, ignored otherwise.</summary>
        public int? BriefId { get; set; }

        public string? RequestedBy { get; set; }
    }

    /// <summary>
    /// What the run panel and the progress bar read. The cost fields are
    /// filled in as the job runs, so the same shape serves both the initial
    /// "queued" reply and every poll after it.
    /// </summary>
    public sealed class ValidationJobDto
    {
        public int Id { get; set; }
        public string CheckType { get; set; } = "";
        public string Status { get; set; } = "";
        public int? BriefId { get; set; }
        public string? ModelName { get; set; }
        public string? Provider { get; set; }

        public int ContactCount { get; set; }
        public int ProcessedCount { get; set; }
        public int FailedCount { get; set; }

        public int InputTokens { get; set; }
        public int CachedTokens { get; set; }
        public int OutputTokens { get; set; }
        public int TotalTokens { get; set; }
        public int WebSearchCalls { get; set; }

        public decimal CalculatedCost { get; set; }
        public int CreditsCharged { get; set; }
        public int ElapsedMs { get; set; }

        public string? ErrorMessage { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }

        public bool IsFinished =>
            Status == ValidationJobStatuses.Completed ||
            Status == ValidationJobStatuses.Partial ||
            Status == ValidationJobStatuses.Failed;
    }

    // ------------------------------------------------------------ results

    /// <summary>
    /// One contact's validation state, in the shape the grid columns and the
    /// contact profile panel both read.
    /// </summary>
    public sealed class ContactValidationDto
    {
        public int ContactId { get; set; }

        public int? ContactFitConfidence { get; set; }
        public string? ContactFitComments { get; set; }
        public int? ContactFitBriefId { get; set; }
        public DateTime? ContactFitCheckedAt { get; set; }

        public int? DataIntegrityConfidence { get; set; }
        public string? DataIntegrityComments { get; set; }
        public DateTime? DataIntegrityCheckedAt { get; set; }

        public int? LiveContactConfidence { get; set; }
        public string? LiveContactComments { get; set; }
        public DateTime? LiveContactCheckedAt { get; set; }

        public int? EmailValidityConfidence { get; set; }
        public string? EmailValidityStatus { get; set; }
        public string? EmailValiditySource { get; set; }
        public string? EmailValidityComments { get; set; }
        public DateTime? EmailCheckedAt { get; set; }

        public List<ValidationSourceDto> Sources { get; set; } = new();

        public bool IsVerified { get; set; }
        public DateTime? VerifiedAt { get; set; }
        public string? VerifiedBy { get; set; }
    }

    public sealed class ValidationSourceDto
    {
        public string Label { get; set; } = "";
        public string Url { get; set; } = "";
    }

    public sealed class MarkVerifiedRequestDto
    {
        public int ClientId { get; set; }
        public List<int> ContactIds { get; set; } = new();
        public bool IsVerified { get; set; } = true;
        public string? VerifiedBy { get; set; }
    }

    // ------------------------------------------- model wire format (parsing)

    /// <summary>
    /// One object out of the JSON array a check returns.
    ///
    /// Every score field of every check is declared here and all are optional,
    /// because the three checks return different keys and a model occasionally
    /// answers with the wrong one. Parsing them all and picking the field the
    /// running check asked for is more forgiving than three near-identical
    /// classes, and a stray extra key costs nothing.
    /// </summary>
    public sealed class ValidationResultItemDto
    {
        public string? ID { get; set; }

        public int? ContactFitConfidence { get; set; }
        public string? ContactFitComments { get; set; }

        public int? DataIntegrityConfidence { get; set; }
        public string? DataIntegrityComments { get; set; }

        public int? LiveContactValidityConfidence { get; set; }
        public string? LiveContactValidityComments { get; set; }

        public List<ValidationSourceDto>? Sources { get; set; }

        /// <summary>
        /// The company classification the model established while judging this
        /// contact, cached so the next run at the same employer need not search
        /// again. Only contact fit asks for it.
        /// </summary>
        public string? CompanyClassification { get; set; }
    }
}
