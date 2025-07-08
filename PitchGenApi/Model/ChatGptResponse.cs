namespace PitchGenApi.Model
{
    public class ChatGptResponse
    {
        public class ChatCompletionResponse
        {
            public string Id { get; set; }
            public string Object { get; set; }
            public long Created { get; set; }
            public string Model { get; set; }
            public Choice[] Choices { get; set; }
            public Usage Usage { get; set; }
        }
        public class Choice
        {
            public int Index { get; set; }
            public Message Message { get; set; }
            public string FinishReason { get; set; }
        }

        public class Message
        {
            public string Role { get; set; }
            public string Content { get; set; }
        }

        public class Usage
        {
            public int prompt_tokens { get; set; }
            public int completion_tokens { get; set; }
            public int total_tokens { get; set; }
        }
    }
}
