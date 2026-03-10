namespace PitchGenApi.Model
{
    public class CrmCustomField
    {
        public int id { get; set; }
        public int client_id { get; set; }
        public string field_name { get; set; }
        public string field_key { get; set; }
        public string field_type { get; set; }
        public string? options_json { get; set; }
        public DateTime created_at { get; set; }
    }
}
