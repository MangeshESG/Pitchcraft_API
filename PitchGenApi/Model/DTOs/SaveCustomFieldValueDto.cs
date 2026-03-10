namespace PitchGenApi.Model.DTOs
{
    public class SaveCustomFieldValueDto
    {
        public int ClientId { get; set; }
        public int ContactId { get; set; }
        public int FieldId { get; set; }
        public string Value { get; set; }
    }
}
