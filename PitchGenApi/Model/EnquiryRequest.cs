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

        /// <summary>
        /// When true, the provider is told to answer with one JSON object
        /// rather than free text — OpenAI's and Qwen's Responses API through
        /// text.format, DeepSeek's and Qwen's chat completions through
        /// response_format.
        ///
        /// This is JSON *object* mode, not schema mode: it guarantees the
        /// reply parses and that nothing sits outside the object, and leaves
        /// the field names to the prompt. That is the whole fix for the
        /// "unreadable JSON" generations, which were never truncations — the
        /// model finished and wrote an unescaped quote inside an HTML
        /// attribute, or a closing line of prose after the final brace.
        ///
        /// Opt-in per call because the pitch services are shared: campaign
        /// prompts, LinkedIn messages and the web-search step all want prose,
        /// and forcing JSON on them would return an object where the caller
        /// expects an email.
        ///
        /// Every provider requires the literal word "json" somewhere in the
        /// prompt when this is on, and rejects the call with a 400 otherwise.
        /// Each service checks for it and appends a one-line reminder rather
        /// than failing a generation over a word the blueprint author cannot
        /// be expected to know about.
        /// </summary>
        public bool RequireJsonObject { get; set; }
    }
}
