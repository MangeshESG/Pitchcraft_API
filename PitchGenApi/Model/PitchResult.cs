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
        /// Worth its own field because search, not tokens, is what a research
        /// request costs: a hundred contacts are a fraction of a cent of tokens
        /// but a cent per search on top. Without this the bill cannot be
        /// attributed, only guessed at.
        /// </summary>
        public int WebSearchCalls { get; set; }
    }
}
