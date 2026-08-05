namespace PitchGenApi.Model.DTOs
{
    public class AiModelSettingDto
    {
        public string PurposeKey { get; set; } = "";
        public string Label { get; set; } = "";
        public string Description { get; set; } = "";
        public string ModelName { get; set; } = "";
        public string DefaultModel { get; set; } = "";
    }

    public class UpdateAiModelSettingsRequest
    {
        /// <summary>purpose key → model name. Blank resets to the default.</summary>
        public Dictionary<string, string?> Settings { get; set; } = new();

        public string? UpdatedBy { get; set; }
    }
}
