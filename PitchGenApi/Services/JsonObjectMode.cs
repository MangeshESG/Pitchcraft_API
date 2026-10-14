namespace PitchGenApi.Services
{
    /// <summary>
    /// Asking a provider for one JSON object instead of free text.
    ///
    /// This is JSON *object* mode, not schema mode. It guarantees two things:
    /// the reply parses, and nothing sits outside the object. It deliberately
    /// says nothing about which fields the object carries — the blueprint's
    /// worked example remains the only definition of that, which is what lets
    /// a prompt change the shape without a deployment.
    ///
    /// Those two guarantees are the whole fix for the "unreadable JSON"
    /// generations. None of them were truncations: every provider already
    /// fails a cut-off reply before the parser sees it. They were complete
    /// replies carrying an unescaped quote inside an HTML attribute, or a
    /// friendly closing line after the final brace.
    ///
    /// The two wire shapes are not interchangeable, and sending the wrong one
    /// is a 400 rather than a silent no-op:
    ///
    ///   Responses API        text.format      = { type: json_object }
    ///   Chat completions     response_format  = { type: json_object }
    /// </summary>
    internal static class JsonObjectMode
    {
        /// <summary>
        /// The Responses API value: request["text"] = TextFormat().
        /// Used by OpenAI, and by Qwen's thinking models, which can only be
        /// reached through that endpoint.
        /// </summary>
        public static Dictionary<string, object> TextFormat() => new()
        {
            ["format"] = new Dictionary<string, object> { ["type"] = "json_object" }
        };

        /// <summary>
        /// The chat-completions value: request["response_format"] = ResponseFormat().
        /// Used by DeepSeek and by Qwen's non-thinking models, both of which
        /// are OpenAI-compatible rather than Responses API.
        /// </summary>
        public static Dictionary<string, object> ResponseFormat() => new()
        {
            ["type"] = "json_object"
        };

        /// <summary>
        /// Every provider rejects JSON mode unless the literal word "json"
        /// appears in the prompt — OpenAI, DeepSeek and Model Studio all
        /// document it, and all three answer 400 without it.
        ///
        /// The prompt is a blueprint written in the admin UI, so that word is
        /// one edit away from disappearing, and the author has no way to know
        /// it is load-bearing. Rather than fail a generation over it, the
        /// reminder is appended when it is missing. A blueprint that already
        /// shows a JSON example — which every migrated one does — is handed
        /// back untouched.
        /// </summary>
        public static string EnsureJsonMentioned(string? prompt)
        {
            var text = prompt ?? "";

            if (text.Contains("json", StringComparison.OrdinalIgnoreCase))
                return text;

            return text.TrimEnd()
                 + "\n\nAnswer with one json object and nothing else.";
        }
    }
}
