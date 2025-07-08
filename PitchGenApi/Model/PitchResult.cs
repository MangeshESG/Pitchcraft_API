namespace PitchGenApi.Model
{
    public class PitchResult
    {
        public string Content { get; set; }
        public int PromptTokens { get; set; }
        public int CompletionTokens { get; set; }
        public int TotalTokens { get; set; }
        public decimal CurrentCost { get; set; }
        public bool IsSuccess { get; set; }
    }
}
