namespace PitchGenApi.Model
{
    public class PlaceholderDefinition
    {
        public int Id { get; set; }

        // 🔗 Link to template definition
        public int TemplateDefinitionId { get; set; }

        public string PlaceholderKey { get; set; } = null!;
        public string FriendlyName { get; set; } = null!;
        public string? Description { get; set; }

        public string Category { get; set; } = "Custom";
        public string InputType { get; set; } = "text";
        public string UiSize { get; set; } = "md";

        public bool IsExpandable { get; set; }
        public bool IsRichText { get; set; }
        public bool IsRuntimeOnly { get; set; }

        public string? DefaultValue { get; set; }
        public string? HelpLink { get; set; }

        // ⭐ NEW: Dropdown options (stored as JSON)
        public string? OptionsJson { get; set; }

        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public int? CategorySequence { get; set; }
        public int? PlaceholderSequence { get; set; }
    }
}
