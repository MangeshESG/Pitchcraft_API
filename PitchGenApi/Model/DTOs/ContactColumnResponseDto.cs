namespace PitchGenApi.Model.DTOs
{
    public class ContactColumnResponseDto
    {
        public List<string> ContactColumns { get; set; }
        public List<CustomFieldFullDto> CustomFields { get; set; }
    }
    public class CustomFieldFullDto
    {
        public int Id { get; set; }
        public int client_id { get; set; }
        public string field_name { get; set; }
        public string field_key { get; set; }
        public string field_type { get; set; }
        public string options_json { get; set; }
    }
}
