namespace PitchGenApi.Model
{
    public class PlaceholderDefinition
    {
        public int Id { get; set; }
        public string PlaceholderKey { get; set; } = null!;
        public string FriendlyName { get; set; } = null!;
        public string? Description { get; set; }

        public string Category { get; set; } = "Custom";
        public string InputType { get; set; } = "text";
        public string UiSize { get; set; } = "md";

        public bool IsExpandable { get; set; }
        public bool IsRichText { get; set; }
        public bool IsRuntimeOnly { get; set; }

        public DateTime CreatedAt { get; set; }
    }

}
