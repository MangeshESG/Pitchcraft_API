using System.Collections.Generic;

namespace PitchGenApi.Model.DTOs
{
    public class SavePlaceholderDefinitionsRequest
    {
        public int TemplateDefinitionId { get; set; }

        public List<PlaceholderDefinitionDto> Placeholders { get; set; }
            = new();
    }

    public class PlaceholderDefinitionDto
    {
        public string PlaceholderKey { get; set; } = null!;
        public string FriendlyName { get; set; } = null!;
        public string Category { get; set; } = "Custom";

        public string? Description { get; set; }

        public string InputType { get; set; } = "text";
        public string UiSize { get; set; } = "md";

        public bool IsRuntimeOnly { get; set; }
        public bool IsExpandable { get; set; }
        public bool IsRichText { get; set; }

        // ⭐ DROPDOWN OPTIONS
        public List<string>? Options { get; set; }

        public int CategorySequence { get; set; }
        public int PlaceholderSequence { get; set; }
    }

    public class DeletePlaceholderDefinitionRequest
    {
        public int TemplateDefinitionId { get; set; }
        public string PlaceholderKey { get; set; } = string.Empty;
    }

}
