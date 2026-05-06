namespace PitchGenApi.Model.DTOs
{
    public class DeepSeekGenerateRequest
    {
        public string Prompt { get; set; }

        public string ModelName { get; set; }

        public string TavilySearchTerm { get; set; }
    }
}
