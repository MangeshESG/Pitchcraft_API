namespace PitchGenApi.Model.DTOs
{
    /// <summary>One editable instruction, as the admin page needs it.</summary>
    public class PromptSettingDto
    {
        public string PromptKey { get; set; } = "";
        public string Label { get; set; } = "";
        public string Description { get; set; } = "";

        /// <summary>
        /// The stored text, sent to the model as-is. Empty when nothing has been
        /// saved for this key - there is no compiled-in text behind it.
        /// </summary>
        public string PromptText { get; set; } = "";

        /// <summary>False while the prompt is empty, i.e. the feature is off.</summary>
        public bool IsConfigured { get; set; }

        /// <summary>Placeholders the text may use, listed for the editor.</summary>
        public List<string> Placeholders { get; set; } = new();

        public DateTime? UpdatedAt { get; set; }
        public string? UpdatedBy { get; set; }
    }

    public class UpdatePromptSettingsRequest
    {
        /// <summary>prompt key -> text. Blank clears that prompt.</summary>
        public Dictionary<string, string?> Prompts { get; set; } = new();

        public string? UpdatedBy { get; set; }
    }
}
