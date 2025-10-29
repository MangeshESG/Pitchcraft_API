namespace PitchGenApi.Model
{
    public class EmailTemplates
    {
        public int Id { get; set; }
        public string TemplateName { get; set; } = "";
        public string Subject { get; set; } = "";
        public string Body { get; set; } = "";
    }
}
