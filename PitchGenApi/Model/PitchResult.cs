namespace PitchGenApi.Model
{
    public class PitchResult
    {
        public string Content { get; set; }
        public int PromptTokens { get; set; }
        public int CompletionTokens { get; set; }
        public int TotalTokens { get; set; }
        public decimal CurrentCost { get; set; }
        public bool IsSuccess { get; set; }
        public int SearchTokens { get; set; }

        /// <summary>
        /// Prompt tokens served from the provider's cache, billed at a small
        /// fraction of the miss rate. Reported separately so a caller that
        /// reuses one long instruction across many requests can confirm the
        /// cache is actually being hit.
        /// </summary>
        public int CachedTokens { get; set; }

        /// <summary>
        /// Server-side web searches the model actually performed, counted from
        /// the tool trace.
        ///
        /// Worth its own field for two reasons. It is the only proof a search
        /// happened at all — a model that ignores the web_search tool answers
        /// from memory over HTTP 200, and zero here is the single signal that
        /// separates that from a researched answer. It is also how search-heavy
        /// work is attributed: DeepSeek bills search as the extra model tokens
        /// it consumes rather than a per-call fee, so this count explains a
        /// token bill it does not itself add to.
        /// </summary>
        public int WebSearchCalls { get; set; }

        /// <summary>
        /// What the model searched for and which pages it opened, one entry per
        /// web_search_call action.
        ///
        /// Kept so a research answer can be audited after the fact. Without it a
        /// stored score is an assertion with no way back to what it was based
        /// on, months after the pages themselves have changed.
        /// </summary>
        public List<string> SearchEvidence { get; set; } = new();

        /// <summary>
        /// The distinct output item types the provider returned. Diagnostic
        /// only: when a research call reports no searches, this is what says
        /// whether the model really skipped the search or whether the search
        /// item simply arrived under a name this code does not recognise.
        /// </summary>
        public List<string> OutputItemTypes { get; set; } = new();
    }
}
