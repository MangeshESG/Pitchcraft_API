namespace PitchGenApi.Model
{
    /// <summary>
    /// One row per (client, list column). Holds the client-level list-view layout:
    /// which columns are shown and in what order. The layout is shared by every
    /// list view / segment / saved view of that client.
    /// </summary>
    public class CrmColumnPreference
    {
        public int id { get; set; }
        public int client_id { get; set; }

        /// <summary>
        /// Column identifier used by the table (contact field name such as
        /// "company_name", or the custom attribute's field_name).
        /// </summary>
        public string column_key { get; set; } = string.Empty;

        /// <summary>Display label, kept so the column panel can render a column
        /// even before any row data has loaded.</summary>
        public string? label { get; set; }

        /// <summary>Set when the column comes from crm_custom_fields.</summary>
        public int? custom_field_id { get; set; }

        public bool is_visible { get; set; } = true;

        /// <summary>Zero-based position of the column in the table.</summary>
        public int sort_order { get; set; }

        public DateTime created_at { get; set; }
        public DateTime? updated_at { get; set; }
    }
}
