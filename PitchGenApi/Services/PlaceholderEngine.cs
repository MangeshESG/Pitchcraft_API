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

        /// <summary>
        /// How many times <see cref="ApplyNested"/> will re-run the
        /// substitution. Five is far more nesting than a campaign ever authors,
        /// and the cap is what stops a value that contains its own token
        /// (or two values that reference each other) from looping forever.
        /// </summary>
        public const int MaxNestingDepth = 5;

        /// <summary>
        /// <see cref="Apply"/> repeated until the text stops changing, so a
        /// value that itself contains a placeholder is resolved too.
        ///
        /// Why this is needed: Apply walks the dictionary once, so whether a
        /// nested token resolves depends on the order the keys happen to sit in
        /// — an {example_output_email} holding {hook} fills only if
        /// example_output_email is visited BEFORE hook. Dictionary order comes
        /// from the campaign's saved JSON, so the same blueprint worked or
        /// failed depending on the order the fields were saved in. Repeating
        /// the pass makes it order-independent.
        ///
        /// Reserved for template-side values (the campaign's own placeholder
        /// values). Per-contact runtime data stays on single-pass Apply: a brace
        /// inside a note, a LinkedIn summary or a custom attribute is content,
        /// not a slot to fill.
        /// </summary>
        public static string ApplyNested(
            string blueprint,
            Dictionary<string, string>? values,
            int maxDepth = MaxNestingDepth)
        {
            if (string.IsNullOrEmpty(blueprint) || values == null || values.Count == 0)
                return blueprint ?? "";

            var result = blueprint;

            for (var pass = 0; pass < maxDepth; pass++)
            {
                var next = Apply(result, values);

                // Nothing left to expand. The common case settles here on the
                // second pass.
                if (string.Equals(next, result, StringComparison.Ordinal))
                    return next;

                result = next;
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
        /// {linkedin_conversation} — both sides of the LinkedIn chat: what we
        /// sent AND what the contact replied, interleaved in the order they
        /// happened and labelled with who said what.
        ///
        /// Deliberately separate from {linkedin_messages} rather than an upgrade
        /// of it. A blueprint written to see only its own outbound messages
        /// keeps behaving exactly as it did; asking for the conversation is an
        /// explicit choice made by typing this token.
        /// </summary>
        public const string LinkedInConversationKey = "linkedin_conversation";

        /// <summary>
        /// use_linkedin_conversation — the campaign-level yes/no switch for the
        /// slot above. Same convention as the others: only an explicit "no"
        /// turns it off.
        /// </summary>
        public const string LinkedInConversationToggleKey = "use_linkedin_conversation";

        /// <summary>
        /// {unsubscribe_url} — the contact's own unsubscribe link, minted by
        /// IUnsubscribeRepository and unique per client + contact.
        ///
        /// Opt-in like the LinkedIn slots: the link is only generated when a
        /// blueprint actually contains this token, so a blueprint that never
        /// asks for one costs nothing and writes no token row.
        /// </summary>
        public const string UnsubscribeUrlKey = "unsubscribe_url";

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

        /// <summary>
        /// Is this the campaign's signature block?
        ///
        /// Matched on the key containing "signature" rather than on an exact
        /// name, because the key is whatever the blueprint's author typed:
        /// "signature", "email_signature" and "signature_block" are all in use.
        ///
        /// It matters because the signature is the one campaign value a channel
        /// may want to keep as markup. The email generator fills it with
        /// <see cref="PromptTextCleaner.CleanSignatureHtml"/> so the sign-off
        /// arrives formatted; LinkedIn, which has no formatting to arrive in,
        /// keeps the plain-text path below.
        /// </summary>
        public static bool IsSignatureKey(string? key)
            => !string.IsNullOrWhiteSpace(key)
               && key.Contains("signature", StringComparison.OrdinalIgnoreCase);

        public static string CleanValue(string key, string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return value ?? "";

            // Healed before the markup goes, not after: the editor can leave a
            // tag between a placeholder brace and its name, and stripping tags
            // closes that gap only when the tag is the sole thing in the way.
            value = PromptTextCleaner.HealSplitPlaceholders(value);

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
