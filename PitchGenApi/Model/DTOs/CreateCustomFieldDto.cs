namespace PitchGenApi.Model.DTOs
{
    public class CreateCustomFieldDto
    {
        public int ClientId { get; set; }
        public string FieldName { get; set; }
        public string FieldKey { get; set; }
        public string FieldType { get; set; }
        public string? OptionsJson { get; set; }
    }
}
