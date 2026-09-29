namespace PitchGenApi.Model
{
    public class EnquiryRequest
    {
        public string Prompt { get; set; }  // The main user input prompt
        public string ScrappedData { get; set; }  // Optional: Additional data (e.g., from web scraping)
        public string ModelName { get; set; } // New property for model name

        /// <summary>
        /// Output budget for this one call, overriding the model's ModelRates
        /// value. Null keeps the rate row's setting, which is what every
        /// email-writing caller wants.
        ///
        /// Needed because ModelRates.MaxTokens is tuned for writing a single
        /// email, and a caller that asks for one JSON object per contact in a
        /// batch of fifty needs many times that. Too small a budget does not
        /// error — it truncates the JSON mid-array — so the batch size has to
        /// be able to set it.
        /// </summary>
        public int? MaxTokens { get; set; }

        /// <summary>
        /// When true, the generation call is sent without the provider's
        /// built-in web search tool. Set by callers that already ran a
        /// dedicated search step and passed its result in the prompt, so the
        /// model should write from that rather than search (and bill) again.
        /// </summary>
        public bool DisableWebSearchTool { get; set; }
    }
}
