namespace PitchGenApi.Model
{
    public class ContactCustomFieldValue
    {
        public int id { get; set; }
        public int client_id { get; set; }
        public int contact_id { get; set; }
        public int field_id { get; set; }
        public string? value { get; set; }
        public DateTime created_at { get; set; }
    }
}
