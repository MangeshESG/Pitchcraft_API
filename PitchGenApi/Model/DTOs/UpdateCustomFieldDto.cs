namespace PitchGenApi.Model.DTOs
{
    public class UpdateCustomFieldDto
    {
        public int Id { get; set; }   // 👈 ADD THIS
        public string FieldName { get; set; }
        public string FieldType { get; set; }
        public string OptionsJson { get; set; }
    }
}
