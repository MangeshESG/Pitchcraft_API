namespace PitchGenApi.Model
{
    /// <summary>
    /// Models the API picks on its own, as opposed to the model a user chooses
    /// on a blueprint. Kept in one place so a change here reaches every caller.
    /// </summary>
    public static class AiModelDefaults
    {
        /// <summary>
        /// Model used for the web-search step of email generation.
        /// </summary>
        public const string WebSearchModel = "gpt-5.6-luna";

        /// <summary>
        /// OpenAI's "*-search-preview" models have web search built in and are
        /// called through Chat Completions with "web_search_options". Every other
        /// model has to go through the Responses API with an explicit
        /// "web_search_preview" tool instead.
        /// </summary>
        public static bool IsSearchPreviewModel(string? modelName) =>
            modelName?.Contains("search-preview") == true;
    }
}
