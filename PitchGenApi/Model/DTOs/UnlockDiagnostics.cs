using System.Text.Json.Nodes;

namespace PitchGenApi.Model.DTOs
{
    /// <summary>
    /// What actually happened during one unlock, for the extension's admin-only
    /// debug view. Never attached to a non-admin response: it carries the raw
    /// prompt and the raw model output.
    /// </summary>
    public sealed class UnlockDiagnostics
    {
        /// <summary>
        /// "cache", "prospeo", "ai" or "hunter" - the mode that produced the
        /// email.
        /// </summary>
        public string Mode { get; set; } = "";

        /// <summary>Plain-language reason this mode was the one that ran.</summary>
        public string ModeReason { get; set; } = "";

        /// <summary>Every mode tried, in order, with the outcome of each.</summary>
        public List<UnlockStageDiagnostics> Stages { get; set; } = new();

        public UnlockProspeoDiagnostics? Prospeo { get; set; }

        public UnlockAiDiagnostics? Ai { get; set; }

        /// <summary>
        /// Set only when the AI stage was not confident enough and Hunter was
        /// asked as well. Null on every unlock that never reached it.
        /// </summary>
        public UnlockHunterDiagnostics? Hunter { get; set; }

        public int ElapsedMs { get; set; }
    }

    public sealed class UnlockStageDiagnostics
    {
        /// <summary>"cache", "prospeo", "ai" or "hunter".</summary>
        public string Name { get; set; } = "";

        /// <summary>"hit", "miss", "skipped" or "error".</summary>
        public string Outcome { get; set; } = "";

        public string Detail { get; set; } = "";

        public int ElapsedMs { get; set; }
    }

    public sealed class UnlockProspeoDiagnostics
    {
        public bool ApiKeyConfigured { get; set; }

        public string Endpoint { get; set; } = "";

        /// <summary>The body posted to Prospeo, minus the API key header.</summary>
        public string RequestBody { get; set; } = "";

        public int? HttpStatus { get; set; }

        /// <summary>The verbatim Prospeo response body.</summary>
        public string? RawResponse { get; set; }

        public bool? Revealed { get; set; }

        public string? EmailStatus { get; set; }

        /// <summary>Why this response was rejected, when it was.</summary>
        public string? RejectedBecause { get; set; }
    }

    public sealed class UnlockAiDiagnostics
    {
        /// <summary>"OpenAI" or "DeepSeek" - whichever the model name routed to.</summary>
        public string Provider { get; set; } = "";

        public string Model { get; set; } = "";

        /// <summary>The complete prompt sent to the model.</summary>
        public string Prompt { get; set; } = "";

        /// <summary>The model's unparsed reply.</summary>
        public string Raw { get; set; } = "";

        /// <summary>
        /// The reply parsed into candidate addresses. Held as a System.Text.Json
        /// node, not a Newtonsoft JArray: the API serialises responses with
        /// System.Text.Json, which cannot render a JArray as an array.
        /// </summary>
        public JsonNode? Results { get; set; }

        /// <summary>
        /// The employer facts the instruction asks for alongside the addresses.
        /// Null when the reply carried no company block; individual fields are
        /// null when the model could not source them.
        /// </summary>
        public UnlockAiCompany? Company { get; set; }

        /// <summary>Which candidate was picked, and why it beat the others.</summary>
        public string? ChosenEmail { get; set; }

        public string? ChoiceReason { get; set; }

        public bool IsSuccess { get; set; }

        public UnlockAiUsage? Usage { get; set; }
    }

    /// <summary>
    /// The employer behind the address, as reported by the email search. Shown
    /// in the extension and saved with the contact.
    /// </summary>
    public sealed class UnlockAiCompany
    {
        public string? Website { get; set; }

        public string? Industry { get; set; }

        /// <summary>A headcount band, e.g. "501-1000".</summary>
        public string? Size { get; set; }
    }

    /// <summary>
    /// The Hunter.io stage: why it ran, what it was asked, and whether its
    /// answer beat the model's.
    /// </summary>
    public sealed class UnlockHunterDiagnostics
    {
        public bool ApiKeyConfigured { get; set; }

        public string Endpoint { get; set; } = "";

        /// <summary>The request URL, with the API key stripped out.</summary>
        public string RequestUrl { get; set; } = "";

        public int? HttpStatus { get; set; }

        /// <summary>The verbatim Hunter response body.</summary>
        public string? RawResponse { get; set; }

        /// <summary>The AI confidence that fell short and triggered this stage.</summary>
        public int TriggeredAtConfidence { get; set; }

        /// <summary>The threshold the AI confidence was measured against.</summary>
        public int ConfidenceThreshold { get; set; }

        /// <summary>Why this stage ran at all.</summary>
        public string TriggerReason { get; set; } = "";

        public string? Email { get; set; }

        /// <summary>Hunter's 0-100 score, comparable to the AI confidence.</summary>
        public int Score { get; set; }

        public string? VerificationStatus { get; set; }

        public string? Domain { get; set; }

        /// <summary>
        /// Which input that domain came from. A lookup that searched the wrong
        /// company is indistinguishable from one that searched the right company
        /// and found nobody, unless this is recorded.
        /// </summary>
        public string? DomainSource { get; set; }

        public string? Position { get; set; }

        public int SourceCount { get; set; }

        /// <summary>Why Hunter's answer was unusable, when it was.</summary>
        public string? RejectedBecause { get; set; }

        /// <summary>True when the address returned to the caller came from Hunter.</summary>
        public bool Preferred { get; set; }

        /// <summary>Which of the two answers won, and why.</summary>
        public string? ComparisonReason { get; set; }
    }

    public sealed class UnlockAiUsage
    {
        public int PromptTokens { get; set; }
        public int CompletionTokens { get; set; }
        public int SearchTokens { get; set; }
        public int TotalTokens { get; set; }
        public decimal CurrentCost { get; set; }
    }
}
