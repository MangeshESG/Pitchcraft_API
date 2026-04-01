namespace PitchGenApi.Model.DTOs
{
    public class BulkUpdateFieldDto
    {
        public List<int> ContactIds { get; set; }

        public string FieldName { get; set; }  // for normal field
        public string Value { get; set; }

        public bool IsCustom { get; set; }

        public int? FieldId { get; set; } // required if custom
    }
}
