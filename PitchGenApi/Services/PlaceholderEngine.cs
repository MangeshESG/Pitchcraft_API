using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace PitchGenApi.Services
{
    /// <summary>
    /// The blueprint placeholder substitution used by every channel. Lifted
    /// verbatim from EmailGenerationController so LinkedIn messages fill
    /// {first_name}, {notes}, {linkedin_info} … exactly the way emails do.
    /// EmailGenerationController still carries its own private copies; switching
    /// it over to this class is a safe follow-up.
    /// </summary>
    public static class PlaceholderEngine
    {
        /// <summary>
        /// Replaces every {key} with its value, case-insensitively.
        /// </summary>
        public static string Apply(string blueprint, Dictionary<string, string>? values)
        {
            if (string.IsNullOrEmpty(blueprint) || values == null || values.Count == 0)
                return blueprint ?? "";

            string result = blueprint;
            foreach (var (key, value) in values)
            {
                var replacement = value ?? "";

                // MatchEvaluator (not the string overload) so "$" inside notes,
                // email bodies or LinkedIn summaries isn't treated as a regex
                // substitution token.
                result = Regex.Replace(
                    result,
                    $"{{{Regex.Escape(key)}}}",
                    _ => replacement,
                    RegexOptions.IgnoreCase
                );
            }
            return result;
        }

        public static bool Contains(string? text, string key)
            => !string.IsNullOrEmpty(text) &&
               text.Contains("{" + key + "}", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// {linkedin_messages} — the slot a blueprint writes where every
        /// LinkedIn message marked as sent to the contact should appear. Filled
        /// identically by the email generator and the LinkedIn generator.
        ///
        /// Opt-in by design: the text is only resolved when a blueprint actually
        /// contains this token, so existing blueprints are untouched.
        /// </summary>
        public const string LinkedInHistoryKey = "linkedin_messages";

        /// <summary>
        /// use_linkedin_message — the campaign-level yes/no switch for the slot
        /// above, read from the blueprint's saved placeholder values (not from
        /// the blueprint text). Same convention as use_email_history: only an
        /// explicit "no" turns it off.
        /// </summary>
        public const string LinkedInHistoryToggleKey = "use_linkedin_message";

        /// <summary>
        /// Reads the yes/no switch out of a blueprint's saved placeholder values.
        /// </summary>
        public static bool IsHistoryEnabled(
            IReadOnlyDictionary<string, string> campaignPlaceholderValues,
            string toggleKey)
        {
            var setting = campaignPlaceholderValues.TryGetValue(toggleKey, out var raw)
                ? (raw ?? "").Trim().ToLowerInvariant()
                : "";

            return setting != "no";
        }

        // Campaign placeholder values are authored in rich-text fields, so they
        // arrive as HTML. The model only needs the words — sending the markup
        // burns tokens and buries the instruction.
        private static readonly HashSet<string> ExampleOutputKeys =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "example_output_email",
                "example_output"
            };

        public static string CleanValue(string key, string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return value ?? "";

            if (ExampleOutputKeys.Contains(key))
                return PromptTextCleaner.CleanEmailBody(value, maxChars: 0);

            return PromptTextCleaner.LooksLikeHtml(value)
                ? PromptTextCleaner.StripHtml(value)
                : value;
        }

        /// <summary>
        /// Appends a resolved input the blueprint had no placeholder for, so the
        /// value reaches the model instead of being silently dropped.
        /// </summary>
        public static string AppendContextSection(string prompt, string label, string content)
            => string.IsNullOrWhiteSpace(content)
                ? prompt
                : $"{prompt}\n\n{label}\n{content.Trim()}";

        /// <summary>
        /// Did a resolved input really land in the prompt? Compared on a slice
        /// rather than the whole value, because substitution trims and re-wraps.
        /// </summary>
        public static bool PromptContains(string prompt, string? value)
        {
            var probe = (value ?? "").Trim();

            if (probe.Length == 0)
                return false;

            if (probe.Length > 120)
                probe = probe[..120];

            return prompt.Contains(probe, StringComparison.Ordinal);
        }

        private static readonly Regex PlaceholderPattern =
            new(@"\{([a-z0-9_\-]{2,60})\}", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Placeholders the blueprint asked for that nothing filled in. A literal
        /// {something} reaching the model is always a bug, so surface it.
        /// </summary>
        public static List<string> FindUnresolved(string prompt)
            => PlaceholderPattern.Matches(prompt)
                .Select(m => m.Groups[1].Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
    }
}
