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
        /// OpenAI-compatible base, up to and including "/compatible-mode/v1",
        /// in the workspace-scoped form:
        ///
        ///   https://{WorkspaceId}.cn-hongkong.maas.aliyuncs.com/compatible-mode/v1     China (Hong Kong)
        ///   https://{WorkspaceId}.eu-central-1.maas.aliyuncs.com/compatible-mode/v1    Germany (Frankfurt)
        ///   https://{WorkspaceId}.ap-northeast-1.maas.aliyuncs.com/compatible-mode/v1  Japan (Tokyo)
        ///   https://{WorkspaceId}.us-east-1.maas.aliyuncs.com/compatible-mode/v1       US (Virginia)
        ///   https://{WorkspaceId}.ap-southeast-1.maas.aliyuncs.com/compatible-mode/v1  Singapore
        ///   https://{WorkspaceId}.cn-beijing.maas.aliyuncs.com/compatible-mode/v1      China (Beijing)
        ///
        /// Three things about this are not interchangeable, and all three have
        /// to move together when the region does:
        ///
        /// 1. The API key is bound to the region it was created in. A key from
        ///    one region returns 403 against another region's host, so there is
        ///    no such thing as switching this URL on its own.
        /// 2. The model list differs by region. Web search in particular only
        ///    covers the Qwen3.8 series outside Singapore and Beijing — the 3.6
        ///    models are not searchable in Hong Kong, Frankfurt, Tokyo or
        ///    Virginia, so the ModelRates rows have to match the region.
        /// 3. SearchCostPerThousandCalls below is region-priced.
        ///
        /// The older aliases — dashscope-intl.aliyuncs.com (Singapore) and
        /// dashscope.aliyuncs.com (Beijing) — still serve the same API, but
        /// Model Studio stops adding features to those hosts after
        /// 2026-09-30, so new configuration should use the form above.
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
        /// The published rate is region-dependent and the gap is not marginal:
        /// $0.573411 per 1,000 calls in Beijing, US Virginia, Hong Kong, Tokyo
        /// and Frankfurt, against $10.00 in Singapore — about seventeen times
        /// more. On a measured run that premium was roughly three quarters of
        /// the entire bill, which is why this is a setting rather than a
        /// constant: it has to be kept in step with whichever BaseUrl region is
        /// actually in use, and getting it wrong misreports cost without
        /// failing anything.
        /// </summary>
        public decimal SearchCostPerThousandCalls { get; set; } = 0.573411m;

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
