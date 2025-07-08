namespace PitchGenApi.Model
{
    public class Prompt
    {
        public int Id { get; set; }
        public string Name { get; set; }

        public string Text { get; set; }
        public int UserId { get; set; }
        public DateTime? CreatedAt { get; set; }
        public string Template { get; set; }
    }
}
