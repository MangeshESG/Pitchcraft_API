namespace PitchGenApi.Model
{
    /// <summary>
    /// Fallback models for each AI purpose. The live values come from
    /// ai_model_settings (see IAiModelSettingsService); these are what the API
    /// uses when a purpose has no row yet, so a fresh database still works.
    /// </summary>
    public static class AiModelDefaults
    {
        /// <summary>
        /// Model used for the web-search step of email generation.
        /// </summary>
        public const string WebSearchModel = "gpt-5.6-luna";

        /// <summary>
        /// Model used by the blueprint builder conversation and example output.
        /// </summary>
        public const string BlueprintGenerationModel = "gpt-5.1";

        /// <summary>
        /// Model used to write email bodies and subject lines.
        /// Being non-GPT, this also means the explicit web-search step runs
        /// before generation instead of the model searching natively.
        /// </summary>
        public const string EmailGenerationModel = "deepseek-v4-flash";

        /// <summary>
        /// Model used to answer contact Q&amp;A questions.
        /// </summary>
        public const string ContactQAModel = "gpt-4o-mini";

        /// <summary>
        /// Model used by the find-email research step. Like web search it has to
        /// reach the live web, so the default is the same searching model.
        /// </summary>
        public const string FindEmailModel = "gpt-5.6-luna";

        public static string ForPurpose(string purposeKey) =>
            purposeKey switch
            {
                AiModelPurposes.WebSearch => WebSearchModel,
                AiModelPurposes.BlueprintGeneration => BlueprintGenerationModel,
                AiModelPurposes.EmailGeneration => EmailGenerationModel,
                AiModelPurposes.ContactQA => ContactQAModel,
                AiModelPurposes.FindEmail => FindEmailModel,
                _ => EmailGenerationModel
            };

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
