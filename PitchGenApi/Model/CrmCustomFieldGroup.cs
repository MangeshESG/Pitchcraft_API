using System.ComponentModel.DataAnnotations.Schema;

namespace PitchGenApi.Model
{
    [Table("crm_custom_field_groups")]
    public class CrmCustomFieldGroup
    {
        public int id { get; set; }
        public int client_id { get; set; }
        public string name { get; set; } = string.Empty;
        public string? description { get; set; }
        public long sort_order { get; set; }
        public DateTime created_at { get; set; }
        public DateTime updated_at { get; set; }
    }
}
