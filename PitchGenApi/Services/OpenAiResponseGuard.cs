using Newtonsoft.Json.Linq;

namespace PitchGenApi.Services
{
    /// <summary>
    /// The Responses API answers with HTTP 200 even when generation was cut short.
    /// On reasoning models the reasoning tokens are drawn from the same max_output_tokens
    /// budget and are emitted before any visible text, so a budget that is too small yields
    /// a "successful" response carrying no usable content. These helpers turn that into an
    /// explicit failure instead of an empty answer.
    /// </summary>
    internal static class OpenAiResponseGuard
    {
        /// <summary>
        /// The incomplete_details reason when the response was cut short, otherwise null.
        /// </summary>
        public static string? GetIncompleteReason(JObject parsed)
        {
            var status = parsed["status"]?.ToString();

            if (!string.Equals(status, "incomplete", StringComparison.OrdinalIgnoreCase))
                return null;

            var reason = parsed["incomplete_details"]?["reason"]?.ToString();

            return string.IsNullOrWhiteSpace(reason) ? "unknown" : reason;
        }

        /// <summary>
        /// Message for a caller that expected content but got none.
        /// </summary>
        public static string DescribeEmptyOutput(string? incompleteReason, int maxTokens)
        {
            if (incompleteReason == "max_output_tokens")
                return $"Response truncated: hit max_output_tokens ({maxTokens}). "
                     + "Reasoning models spend this budget before emitting any text - "
                     + "increase MaxTokens for this model in ModelRates.";

            if (incompleteReason != null)
                return $"Response incomplete: {incompleteReason}.";

            return "Model returned no content.";
        }
    }
}
