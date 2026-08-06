namespace PitchGenApi.Model
{
    /// <summary>
    /// One row per AI purpose (web search, blueprint generation, email
    /// generation, contact Q&amp;A). Admins edit these from Settings > AI models
    /// and every API that calls a model reads its model name from here.
    /// </summary>
    public class AiModelSetting
    {
        public int id { get; set; }

        /// <summary>One of <see cref="AiModelPurposes"/>.</summary>
        public string purpose_key { get; set; } = "";

        public string model_name { get; set; } = "";

        public DateTime updated_at { get; set; }

        public string? updated_by { get; set; }
    }
}
