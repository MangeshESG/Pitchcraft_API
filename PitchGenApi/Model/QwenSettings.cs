namespace PitchGenApi.Model
{
    /// <summary>
    /// Alibaba Cloud Model Studio (DashScope) credentials for the Qwen models.
    /// Bound from the "QwenSettings" section of appsettings.json.
    /// </summary>
    public class QwenSettings
    {
        public string ApiKey { get; set; } = string.Empty;

        /// <summary>
        /// OpenAI-compatible base, up to and including "/compatible-mode/v1".
        /// Both endpoint shapes work:
        ///   https://dashscope-intl.aliyuncs.com/compatible-mode/v1              (Singapore / international)
        ///   https://dashscope.aliyuncs.com/compatible-mode/v1                   (China Beijing)
        ///   https://{WorkspaceId}.ap-southeast-1.maas.aliyuncs.com/compatible-mode/v1
        ///   https://{WorkspaceId}.cn-beijing.maas.aliyuncs.com/compatible-mode/v1
        /// The workspace-scoped maas.aliyuncs.com form is the one Model Studio
        /// documents now; the dashscope hosts are the older aliases and still
        /// serve the same API.
        /// </summary>
        public string BaseUrl { get; set; } = string.Empty;

        /// <summary>
        /// What one server-side web search costs, in USD per 1,000 calls.
        ///
        /// This exists because Qwen bills search differently from DeepSeek.
        /// DeepSeek charges search purely as the extra model tokens it consumes,
        /// so counting tokens captures the whole bill. Model Studio charges the
        /// tokens *and* a separate per-call search fee, so a cost built from
        /// tokens alone under-reports every researched contact.
        ///
        /// The published agent-policy rate is region-dependent and the gap is
        /// large — $10.00 per 1,000 calls in Singapore against $0.573411 in
        /// Beijing, US Virginia, Hong Kong, Tokyo and Frankfurt. It is a setting
        /// rather than a constant so it can be kept in step with whichever
        /// BaseUrl region above is actually in use.
        /// </summary>
        public decimal SearchCostPerThousandCalls { get; set; } = 10.00m;

        /// <summary>
        /// Search scale passed as search_options.search_strategy on the
        /// chat-completions fallback path.
        ///
        /// Must not be "agent". Model Studio's guide reads as though agent were
        /// the strategy to use in non-thinking mode, but the live API does the
        /// opposite: measured 2026-09-11, agent puts the model into thinking
        /// mode and the request is then refused outright with
        /// "Non-streaming mode does not support Web Search in thinking mode"
        /// (HTTP 400) — on qwen-plus and qwen3.6-flash alike, and regardless of
        /// enable_thinking being sent as false. Every call on this path is
        /// non-streaming, so agent fails all of them.
        ///
        /// "turbo" was verified working non-streaming and is what Model Studio
        /// falls back to when search_options is omitted entirely.
        /// </summary>
        public string SearchStrategy { get; set; } = "turbo";
    }
}
