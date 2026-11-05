using System.Net;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;

namespace PitchGenApi.Services
{
    /// <summary>
    /// One source highlight: a verbatim slice of the email's visible text,
    /// the source that owns it, and the tooltip shown on hover.
    ///
    /// The colour is deliberately absent. The owner is the stable key; the UI
    /// maps it to a colour, so restyling never means re-running a generation.
    /// </summary>
    public sealed class EmailHighlight
    {
        public string Text { get; set; } = "";
        public string Owner { get; set; } = "";
        public string Label { get; set; } = "";

        /// <summary>
        /// Which occurrence of <see cref="Text"/> to paint when the same
        /// wording appears more than once, 1-based. 1 for the usual case.
        /// </summary>
        public int Occurrence { get; set; } = 1;
    }

    /// <summary>
    /// One complete, sendable email the generation produced.
    ///
    /// A blueprint asks for between one and five of these in a single call
    /// (Email Option Count), and each one carries its own subject, its own
    /// body and its own highlights — a highlight never crosses from one
    /// option to another, because the wording it quotes differs.
    /// </summary>
    public sealed class GeneratedEmailOption
    {
        /// <summary>
        /// Two to five plain words naming this option's angle. Metadata for
        /// the picker in the UI; it never appears in the email itself.
        /// </summary>
        public string Label { get; set; } = "";

        public string Subject { get; set; } = "";
        public string BodyHtml { get; set; } = "";
        public List<EmailHighlight> Highlights { get; set; } = new();

        /// <summary>
        /// Highlights this option returned whose text is not in its own body.
        /// Dropped for the reason given on <see cref="GeneratedEmail.UnmatchedHighlights"/>.
        /// </summary>
        public int UnmatchedHighlights { get; set; }
    }

    /// <summary>
    /// What one email-generation call produced, after the reply has been read
    /// as either the structured JSON object or a bare HTML body.
    /// </summary>
    public sealed class GeneratedEmail
    {
        /// <summary>
        /// Every option the reply carried, in the order the model ranked them.
        /// Empty only when the reply could not be read as the JSON contract.
        ///
        /// A blueprint that asks for one email produces one entry here, so a
        /// caller that only ever wants the primary email can ignore this and
        /// keep reading <see cref="BodyHtml"/>.
        /// </summary>
        public List<GeneratedEmailOption> Options { get; set; } = new();

        /// <summary>The primary option's body — Options[0].</summary>
        public string BodyHtml { get; set; } = "";

        /// <summary>The primary option's subject — Options[0].</summary>
        public string Subject { get; set; } = "";

        /// <summary>The primary option's highlights — Options[0].</summary>
        public List<EmailHighlight> Highlights { get; set; } = new();

        /// <summary>
        /// True when the reply parsed as the JSON contract. False means the
        /// model returned plain HTML — an un-migrated blueprint, or a reply
        /// the parser could not read — and the caller must fall back to the
        /// separate subject call and to highlights baked into the body.
        /// </summary>
        public bool IsStructured { get; set; }

        /// <summary>
        /// True when the reply was clearly meant to be the JSON object but
        /// could not be read, and nothing could be salvaged from it — almost
        /// always a generation that ran out of output budget before a single
        /// option closed.
        ///
        /// Worth separating from an ordinary unstructured reply: the text is
        /// half an object, not an email, so saving it as the body would store
        /// visible JSON for the prospect to receive.
        /// </summary>
        public bool LooksTruncated { get; set; }

        /// <summary>
        /// True when the reply stopped mid-array and the options that had
        /// already closed were recovered from it.
        ///
        /// Several full bodies share one output budget, so a run asking for
        /// four options is far likelier to be cut short than one asking for a
        /// single email. The options that did arrive complete are real emails
        /// and worth keeping — the alternative is failing the whole call and
        /// charging another credit for work already done. Separate from
        /// <see cref="LooksTruncated"/>, which means nothing was salvageable.
        /// </summary>
        public bool RecoveredFromTruncation { get; set; }

        /// <summary>
        /// True when the reply parsed as the JSON contract and was complete,
        /// but carried no usable option — an empty options array, or every
        /// body_html empty.
        ///
        /// Nothing was cut short here, so this must not be reported as a
        /// truncation: the output budget is not the problem and raising it
        /// changes nothing. The model declined to write, which points at the
        /// blueprint or the inputs it was given.
        /// </summary>
        public bool IsEmptyBody { get; set; }

        /// <summary>
        /// Options the reply carried that had no body at all. They are
        /// dropped rather than shown as empty choices in the picker.
        /// </summary>
        public int DroppedEmptyOptions { get; set; }

        /// <summary>
        /// Highlights the model returned whose text could not be found in the
        /// body of the option that returned them. They are dropped rather
        /// than shipped, because a snippet that does not match is invisible
        /// in the UI and unexplainable later. The count is the quality signal
        /// worth watching after a prompt change.
        /// </summary>
        public int UnmatchedHighlights { get; set; }
    }

    /// <summary>
    /// Reads the email-generation reply.
    ///
    /// The generator is asked for one JSON object carrying an "options" array,
    /// each entry holding a subject, clean body HTML and highlight records.
    /// Two older shapes still arrive and both keep working: a flat object with
    /// subject/body_html/highlights becomes a single option, and a bare HTML
    /// email from an un-migrated blueprint is handed back untouched as the
    /// body — that is what keeps those blueprints generating exactly as before.
    /// </summary>
    public static class GeneratedEmailParser
    {
        public static GeneratedEmail Parse(string? content)
        {
            var raw = content ?? "";

            var text = PrepareJsonText(raw);
            var json = TryParseObject(text);
            var recovered = false;

            // Nothing parsed. Before giving up, try to lift the options that
            // did close out of a reply that stopped mid-array.
            if (json == null && text.Length > 0)
            {
                json = TryRecoverTruncatedOptions(text);
                recovered = json != null;
            }

            var options = json == null ? new List<GeneratedEmailOption>() : ReadOptions(json);

            // A JSON object with no usable option is not a usable structured
            // reply — treat the whole thing as HTML rather than saving an
            // empty email.
            //
            // Which of the two failures it is matters to the caller. A reply
            // that did not parse at all may well have been cut off mid-object.
            // One that parsed cleanly and simply carries no body was complete
            // when it arrived, so the output budget is not involved and only
            // the first of these may be called a truncation.
            if (options.Count == 0)
            {
                return new GeneratedEmail
                {
                    BodyHtml = raw.Trim(),
                    IsStructured = false,
                    IsEmptyBody = json != null,
                    LooksTruncated = json == null && LooksLikeBrokenJson(raw)
                };
            }

            var primary = options[0];

            var result = new GeneratedEmail
            {
                Options = options,
                BodyHtml = primary.BodyHtml,
                Subject = primary.Subject,
                Highlights = primary.Highlights,
                IsStructured = true,
                RecoveredFromTruncation = recovered,
                UnmatchedHighlights = options.Sum(option => option.UnmatchedHighlights),
                DroppedEmptyOptions = CountOptionObjects(json!) - options.Count
            };

            if (result.UnmatchedHighlights > 0)
            {
                Log.Warning(
                    "Email generation returned {Unmatched} highlight(s) whose text is not in the body of the "
                    + "option that returned them; they were dropped. Kept {Kept} across {Options} option(s).",
                    result.UnmatchedHighlights,
                    options.Sum(option => option.Highlights.Count),
                    options.Count);
            }

            if (result.DroppedEmptyOptions > 0)
            {
                Log.Warning(
                    "Email generation returned {Dropped} option(s) with no body; they were dropped. Kept {Kept}.",
                    result.DroppedEmptyOptions, options.Count);
            }

            if (recovered)
            {
                Log.Warning(
                    "Email generation reply stopped mid-array; {Kept} complete option(s) were recovered from it. "
                    + "Raise MaxTokens for this model if it keeps happening.",
                    options.Count);
            }

            return result;
        }

        /// <summary>
        /// Serializes highlights for the contacts row. Returns null for an
        /// empty set so the column reads as "nothing to paint" rather than as
        /// an empty array that looks like a failed generation.
        /// </summary>
        public static string? Serialize(List<EmailHighlight> highlights)
            => highlights.Count == 0 ? null : JsonConvert.SerializeObject(highlights);

        /// <summary>
        /// Did this reply set out to be the JSON object and fail? A bare HTML
        /// email from an un-migrated blueprint starts with a tag and never
        /// mentions the contract's field names, so the two are easy to tell
        /// apart without guessing.
        /// </summary>
        private static bool LooksLikeBrokenJson(string raw)
        {
            var text = raw.TrimStart().TrimStart('`');

            return text.StartsWith("{", StringComparison.Ordinal)
                || text.StartsWith("json", StringComparison.OrdinalIgnoreCase)
                || raw.Contains("\"body_html\"", StringComparison.OrdinalIgnoreCase)
                || raw.Contains("\"options\"", StringComparison.OrdinalIgnoreCase)
                || raw.Contains("\"highlights\"", StringComparison.OrdinalIgnoreCase);
        }

        // ── reply reading ──

        /// <summary>
        /// The names the options array may arrive under. One contract, but a
        /// model that renames the key on a bad day still gets read rather
        /// than costing the user a credit.
        /// </summary>
        private static readonly string[] OptionsKeys =
            { "options", "email_options", "emailOptions", "emails", "variants" };

        /// <summary>
        /// Every usable option in the reply: the options array first, the flat
        /// single-email shape second. An option with no body is dropped —
        /// an empty choice in the picker is not a choice.
        /// </summary>
        private static List<GeneratedEmailOption> ReadOptions(JObject json)
        {
            var options = new List<GeneratedEmailOption>();
            var array = ReadOptionsArray(json);

            if (array != null)
            {
                foreach (var token in array)
                {
                    if (token is not JObject item)
                        continue;

                    var option = ReadOption(item);
                    if (option != null)
                        options.Add(option);
                }

                return options;
            }

            // Legacy flat reply: {"subject":…,"body_html":…,"highlights":[…]}.
            var single = ReadOption(json);
            if (single != null)
                options.Add(single);

            return options;
        }

        private static JArray? ReadOptionsArray(JObject json)
        {
            foreach (var name in OptionsKeys)
            {
                if (json.GetValue(name, StringComparison.OrdinalIgnoreCase) is JArray array)
                    return array;
            }

            return null;
        }

        /// <summary>How many option objects the reply held, usable or not.</summary>
        private static int CountOptionObjects(JObject json)
        {
            var array = ReadOptionsArray(json);
            return array?.OfType<JObject>().Count() ?? 1;
        }

        /// <summary>
        /// One option object. Null when it carries no body, which is the only
        /// field without which there is no email to show.
        /// </summary>
        private static GeneratedEmailOption? ReadOption(JObject item)
        {
            var bodyHtml =
                (ReadString(item, "body_html", "bodyHtml", "body", "email_body", "emailBody") ?? "").Trim();

            if (bodyHtml.Length == 0)
                return null;

            var option = new GeneratedEmailOption
            {
                BodyHtml = bodyHtml,
                Subject = (ReadString(item, "subject", "email_subject", "emailSubject") ?? "").Trim(),
                Label = (ReadString(item, "label", "name", "angle") ?? "").Trim()
            };

            // Each option is checked against its own body. Options differ in
            // wording, so a highlight from one of them usually is not in the
            // next — validating against anything else would drop good records
            // and keep bad ones.
            var bodyText = ToPlainText(bodyHtml);

            if (item.GetValue("highlights", StringComparison.OrdinalIgnoreCase) is JArray highlights)
            {
                foreach (var token in highlights)
                {
                    if (token is not JObject entry)
                        continue;

                    var text = (ReadString(entry, "text", "snippet", "content") ?? "").Trim();
                    if (text.Length == 0)
                        continue;

                    // The snippet must really be in the body. The UI paints by
                    // searching for it, so one that is not there would silently
                    // do nothing — better to drop it here and count it.
                    if (!bodyText.Contains(Normalize(text), StringComparison.OrdinalIgnoreCase))
                    {
                        option.UnmatchedHighlights++;
                        continue;
                    }

                    option.Highlights.Add(new EmailHighlight
                    {
                        Text = text,
                        Owner = (ReadString(entry, "owner", "source", "category") ?? "").Trim(),
                        Label = (ReadString(entry, "label", "tooltip", "title") ?? "").Trim(),
                        Occurrence = ReadInt(entry, "occurrence", "instance") is int n && n > 0 ? n : 1
                    });
                }
            }

            return option;
        }

        private static readonly Regex TagRegex =
            new("<[^>]+>", RegexOptions.Compiled);

        private static readonly Regex WhitespaceRegex =
            new(@"\s+", RegexOptions.Compiled);

        /// <summary>
        /// The body's visible text, whitespace-collapsed, for checking that a
        /// highlight snippet is really present. Matches how the UI searches:
        /// across tag boundaries, so a snippet containing a bold word or a
        /// link still lines up.
        /// </summary>
        private static string ToPlainText(string html)
            => Normalize(WebUtility.HtmlDecode(TagRegex.Replace(html, " ")));

        private static string Normalize(string text)
            => WhitespaceRegex.Replace(WebUtility.HtmlDecode(text), " ").Trim();

        /// <summary>
        /// The JSON object's text, whether it arrived bare, inside a fenced
        /// block, or wrapped in a sentence of prose. Empty when the reply
        /// holds nothing object-shaped at all.
        /// </summary>
        private static string PrepareJsonText(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return "";

            var text = content.Trim();

            if (text.StartsWith("```", StringComparison.Ordinal))
            {
                var firstLineBreak = text.IndexOf('\n');
                if (firstLineBreak >= 0)
                    text = text[(firstLineBreak + 1)..];

                var closingFence = text.LastIndexOf("```", StringComparison.Ordinal);
                if (closingFence >= 0)
                    text = text[..closingFence];

                text = text.Trim();
            }

            if (text.StartsWith("{", StringComparison.Ordinal))
                return text;

            var start = text.IndexOf('{');
            var end = text.LastIndexOf('}');

            return start < 0 || end <= start ? "" : text[start..(end + 1)];
        }

        private static JObject? TryParseObject(string text)
        {
            if (text.Length == 0)
                return null;

            try
            {
                return JsonConvert.DeserializeObject<JObject>(text);
            }
            catch (JsonException)
            {
                // A truncated reply lands here: the output budget ran out
                // mid-array and the JSON never closed. The caller tries the
                // recovery below before giving up.
                return null;
            }
        }

        /// <summary>
        /// Lifts the option objects that closed out of a reply that stopped
        /// mid-array, and rebuilds a valid object from them.
        ///
        /// Only whole objects are taken. An option that closed has a complete
        /// body string behind it, so nothing half-written can reach an email;
        /// the one that was being written when the budget ran out is dropped.
        /// Returns null when not a single option closed — then there really is
        /// nothing to keep.
        /// </summary>
        private static JObject? TryRecoverTruncatedOptions(string text)
        {
            var key = -1;

            foreach (var name in OptionsKeys)
            {
                key = text.IndexOf($"\"{name}\"", StringComparison.OrdinalIgnoreCase);
                if (key >= 0)
                    break;
            }

            if (key < 0)
                return null;

            var open = text.IndexOf('[', key);
            if (open < 0)
                return null;

            var depth = 0;
            var inString = false;
            var escaped = false;
            var lastComplete = -1;

            for (var i = open + 1; i < text.Length; i++)
            {
                var c = text[i];

                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') inString = false;
                    continue;
                }

                if (c == '"')
                {
                    inString = true;
                }
                else if (c == '{')
                {
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                        lastComplete = i;
                }
                else if (c == ']' && depth == 0)
                {
                    break;
                }
            }

            if (lastComplete < 0)
                return null;

            var rebuilt = "{\"options\":[" + text[(open + 1)..(lastComplete + 1)] + "]}";

            return TryParseObject(rebuilt);
        }

        private static string? ReadString(JObject element, params string[] names)
        {
            foreach (var name in names)
            {
                var token = element.GetValue(name, StringComparison.OrdinalIgnoreCase);
                if (token != null && token.Type != JTokenType.Null)
                    return token.ToString();
            }

            return null;
        }

        private static int? ReadInt(JObject element, params string[] names)
        {
            foreach (var name in names)
            {
                var token = element.GetValue(name, StringComparison.OrdinalIgnoreCase);
                if (token == null || token.Type == JTokenType.Null)
                    continue;

                if (int.TryParse(token.ToString(), out var value))
                    return value;
            }

            return null;
        }
    }
}
