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
    /// What one email-generation call produced, after the reply has been read
    /// as either the structured JSON object or a bare HTML body.
    /// </summary>
    public sealed class GeneratedEmail
    {
        public string BodyHtml { get; set; } = "";
        public string Subject { get; set; } = "";
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
        /// could not be read — almost always a generation that ran out of
        /// output budget and stopped mid-array.
        ///
        /// Worth separating from an ordinary unstructured reply: the text is
        /// half an object, not an email, so saving it as the body would store
        /// visible JSON for the prospect to receive.
        /// </summary>
        public bool LooksTruncated { get; set; }

        /// <summary>
        /// True when the reply parsed as the JSON contract and was complete,
        /// but carried no body — the model answered {"body_html":"",...}.
        ///
        /// Nothing was cut short here, so this must not be reported as a
        /// truncation: the output budget is not the problem and raising it
        /// changes nothing. The model declined to write, which points at the
        /// blueprint or the inputs it was given.
        /// </summary>
        public bool IsEmptyBody { get; set; }

        /// <summary>
        /// Highlights the model returned whose text could not be found in the
        /// body. They are dropped rather than shipped, because a snippet that
        /// does not match is invisible in the UI and unexplainable later. The
        /// count is the quality signal worth watching after a prompt change.
        /// </summary>
        public int UnmatchedHighlights { get; set; }
    }

    /// <summary>
    /// Reads the email-generation reply.
    ///
    /// The generator is asked for one JSON object carrying the subject, the
    /// clean body HTML and the highlight records. Older blueprints still
    /// answer with bare HTML that has the highlight spans baked in, so every
    /// reply that does not parse is handed back untouched as the body — that
    /// is what keeps un-migrated blueprints generating exactly as before.
    /// </summary>
    public static class GeneratedEmailParser
    {
        public static GeneratedEmail Parse(string? content)
        {
            var raw = content ?? "";

            var json = ExtractJsonObject(raw);

            var bodyHtml = json == null
                ? ""
                : (ReadString(json, "body_html", "bodyHtml", "body", "email_body", "emailBody") ?? "").Trim();

            // A JSON object with no body is not a usable structured reply —
            // treat the whole thing as HTML rather than saving an empty email.
            //
            // Which of the two failures it is matters to the caller. A reply
            // that did not parse at all may well have been cut off mid-object.
            // One that parsed cleanly and simply has an empty body_html was
            // complete when it arrived, so the output budget is not involved
            // and only the first of these may be called a truncation.
            if (json == null || bodyHtml.Length == 0)
            {
                return new GeneratedEmail
                {
                    BodyHtml = raw.Trim(),
                    IsStructured = false,
                    IsEmptyBody = json != null,
                    LooksTruncated = json == null && LooksLikeBrokenJson(raw)
                };
            }

            var result = new GeneratedEmail
            {
                BodyHtml = bodyHtml,
                Subject = (ReadString(json, "subject", "email_subject", "emailSubject") ?? "").Trim(),
                IsStructured = true
            };

            var bodyText = ToPlainText(bodyHtml);

            if (json["highlights"] is JArray highlights)
            {
                foreach (var token in highlights)
                {
                    if (token is not JObject item)
                        continue;

                    var text = (ReadString(item, "text", "snippet", "content") ?? "").Trim();
                    if (text.Length == 0)
                        continue;

                    // The snippet must really be in the body. The UI paints by
                    // searching for it, so one that is not there would silently
                    // do nothing — better to drop it here and count it.
                    if (!bodyText.Contains(Normalize(text), StringComparison.OrdinalIgnoreCase))
                    {
                        result.UnmatchedHighlights++;
                        continue;
                    }

                    result.Highlights.Add(new EmailHighlight
                    {
                        Text = text,
                        Owner = (ReadString(item, "owner", "source", "category") ?? "").Trim(),
                        Label = (ReadString(item, "label", "tooltip", "title") ?? "").Trim(),
                        Occurrence = ReadInt(item, "occurrence", "instance") is int n && n > 0 ? n : 1
                    });
                }
            }

            if (result.UnmatchedHighlights > 0)
            {
                Log.Warning(
                    "Email generation returned {Unmatched} highlight(s) whose text is not in the body; they were dropped. Kept {Kept}.",
                    result.UnmatchedHighlights, result.Highlights.Count);
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
                || raw.Contains("\"highlights\"", StringComparison.OrdinalIgnoreCase);
        }

        // ── reply reading ──

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
        /// Pulls the JSON object out of a reply, whether it arrived bare,
        /// inside a ```json fence, or wrapped in a sentence of prose.
        /// </summary>
        private static JObject? ExtractJsonObject(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return null;

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

            if (!text.StartsWith("{", StringComparison.Ordinal))
            {
                var start = text.IndexOf('{');
                var end = text.LastIndexOf('}');
                if (start < 0 || end <= start)
                    return null;

                text = text[start..(end + 1)];
            }

            try
            {
                return JsonConvert.DeserializeObject<JObject>(text);
            }
            catch (JsonException)
            {
                // A truncated reply lands here: the output budget ran out
                // mid-array and the JSON never closed. The caller falls back
                // to treating the text as HTML, which is the safe outcome.
                return null;
            }
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
