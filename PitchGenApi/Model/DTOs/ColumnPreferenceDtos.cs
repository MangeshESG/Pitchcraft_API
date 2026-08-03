namespace PitchGenApi.Model.DTOs
{
    public class ColumnPreferenceDto
    {
        public string ColumnKey { get; set; } = string.Empty;
        public string? Label { get; set; }
        public bool IsVisible { get; set; } = true;

        /// <summary>Assigned by the server on save (index in the posted array).</summary>
        public int SortOrder { get; set; }

        /// <summary>crm_custom_fields.id when this column is a custom attribute.</summary>
        public int? CustomFieldId { get; set; }

        public bool IsCustomField { get; set; }
    }

    /// <summary>
    /// Full replace of a client's column layout. The order of <see cref="Columns"/>
    /// is the column order — the server stores the array index as sort_order.
    /// </summary>
    public class SaveColumnPreferencesDto
    {
        public int ClientId { get; set; }
        public List<ColumnPreferenceDto> Columns { get; set; } = new();
    }
}
