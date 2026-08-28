namespace PitchGenApi.Model
{
    /// <summary>
    /// One row per admin-editable AI instruction (Settings &gt; Admin &gt;
    /// Prompts). The compiled-in copy of each instruction stays in code as the
    /// default; a row here overrides it, and deleting the row falls back to the
    /// default again.
    /// </summary>
    public class PromptSetting
    {
        public int id { get; set; }

        /// <summary>One of <see cref="PromptKeys"/>.</summary>
        public string prompt_key { get; set; } = "";

        /// <summary>The full instruction text, placeholders included.</summary>
        public string prompt_text { get; set; } = "";

        public DateTime updated_at { get; set; }

        public string? updated_by { get; set; }
    }
}
