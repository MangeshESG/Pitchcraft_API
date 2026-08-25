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
        /// <summary>"cache", "prospeo" or "ai" - the mode that produced the email.</summary>
        public string Mode { get; set; } = "";

        /// <summary>Plain-language reason this mode was the one that ran.</summary>
        public string ModeReason { get; set; } = "";

        /// <summary>Every mode tried, in order, with the outcome of each.</summary>
        public List<UnlockStageDiagnostics> Stages { get; set; } = new();

        public UnlockProspeoDiagnostics? Prospeo { get; set; }

        public UnlockAiDiagnostics? Ai { get; set; }

        public int ElapsedMs { get; set; }
    }

    public sealed class UnlockStageDiagnostics
    {
        /// <summary>"cache", "prospeo" or "ai".</summary>
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

        /// <summary>Which candidate was picked, and why it beat the others.</summary>
        public string? ChosenEmail { get; set; }

        public string? ChoiceReason { get; set; }

        public bool IsSuccess { get; set; }

        public UnlockAiUsage? Usage { get; set; }
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
