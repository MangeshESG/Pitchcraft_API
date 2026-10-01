using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace PitchGenApi.Services
{
    /// <summary>
    /// Turns stored HTML (past emails, the campaign's example output email,
    /// LinkedIn summaries, notes) into the plain text that actually goes into a
    /// generation prompt.
    ///
    /// Real inbound mail is mostly noise: style blocks, Outlook conditional
    /// comments, tracking pixels, unsubscribe footers and - worst of all - the
    /// full quoted trail of every earlier message, which we already send as its
    /// own entry. Left in, that noise is the bulk of the prompt and it pushes
    /// the parts that matter out of the model's attention. These helpers keep
    /// the readable message and drop the rest.
    /// </summary>
    public static class PromptTextCleaner
    {
        /// <summary>
        /// Per-message body budget used when none is given. 0 means no
        /// truncation: past emails go into the prompt in full, so a reply
        /// never has to be written against a half-quoted message.
        /// </summary>
        public const int DefaultEmailBodyBudget = 0;

        // ---- markup that carries no readable text at all -------------------
        private static readonly Regex NonContentBlocks = new(
            @"<(script|style|head|title|noscript|svg)\b[^>]*>.*?</\1\s*>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        // <!-- ... -->, including Outlook's <!--[if mso]> ... <![endif]--> pairs.
        private static readonly Regex HtmlComments = new(
            @"<!--.*?-->|<!\[endif\]-->|<!\[CDATA\[.*?\]\]>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        private static readonly Regex LineBreakTags = new(
            @"<br\s*/?>|<hr\s*/?>",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex BlockBoundaryTags = new(
            @"</?(p|div|li|ul|ol|tr|table|h[1-6]|blockquote|section|article|header|footer)\b[^>]*>",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex AnyTag = new("<[^>]*>", RegexOptions.Compiled);

        // Leftover CSS that survives when a mail ships styles outside <style>.
        private static readonly Regex CssRuleBlocks = new(
            @"[.#@]?[-\w]+\s*\{[^{}]*(?:font|color|margin|padding|width|height|border|background|display)\s*:[^{}]*\}",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // ---- where the quoted history starts -------------------------------
        // Everything from the first of these markers is a copy of messages that
        // are already in the transcript as their own entries.
        private static readonly Regex[] QuotedTrailMarkers =
        {
            new(@"^[ \t]*-{2,}\s*Original Message\s*-{2,}",
                RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled),

            new(@"^[ \t]*-{3,}\s*Forwarded message\s*-{3,}",
                RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled),

            // Gmail: "On Tue, 3 Jun 2026 at 10:04, Jane <jane@x.com> wrote:"
            // (the "wrote:" often lands on the next line, hence the span).
            new(@"^[ \t]*On\b[^\n]{0,200}(?:\n[^\n]{0,200}){0,2}?\bwrote:[ \t]*$",
                RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled),

            // Outlook header block introducing the quoted mail.
            new(@"^[ \t]*From:[ \t]*\S[^\n]*\n[ \t]*(Sent|Date|To|Subject):",
                RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled),

            // Outlook's horizontal rule between the reply and the quote.
            new(@"^[ \t]*_{10,}[ \t]*$",
                RegexOptions.Multiline | RegexOptions.Compiled),

            new(@"^[ \t]*(Begin forwarded message|Reply above this line)\b",
                RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled),
        };

        // ---- footer / boilerplate lines ------------------------------------
        private static readonly Regex BoilerplateLine = new(
            @"^\s*\[?\s*(?:"
            + @"unsubscribe"
            + @"|opt[- ]?out"
            + @"|manage (?:your )?(?:email )?preferences"
            + @"|update your preferences"
            + @"|view (?:this (?:email|message) )?in (?:your )?browser"
            + @"|(?:if )?you (?:are )?receiv(?:e|ed|ing) this (?:email|message)"
            + @"|you'?re receiving this"
            + @"|no longer wish to receive"
            + @"|all rights reserved"
            + @"|privacy policy"
            + @"|terms of (?:use|service)"
            + @"|powered by\b"
            + @"|sent (?:from|with) my (?:iphone|ipad|android|mobile|phone|blackberry)"
            + @"|(?:©|\(c\))\s*\d{4}"
            + @"|image:\s"
            + @"|cid:"
            + @")",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Tracking / redirect links are long and meaningless to the model.
        private static readonly Regex LongUrl = new(
            @"\bhttps?://\S{60,}", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex DataUri = new(
            @"\bdata:[a-z]+/[a-z0-9.+-]+;base64,[A-Za-z0-9+/=]+",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // A line of nothing but separator characters.
        private static readonly Regex SeparatorOnlyLine = new(
            @"^[\s\-_=*~|>+#•·–—]{3,}$", RegexOptions.Compiled);

        // Zero-width spaces/joiners, word joiner, BOM and soft hyphen - mail
        // templates are full of them and each one costs a token.
        private static readonly Regex InvisibleChars = new(
            @"[​-‏⁠﻿­]", RegexOptions.Compiled);

        /// <summary>
        /// Markup out, readable text in - nothing else is removed. Use this for
        /// values where every word may matter (notes, LinkedIn summaries,
        /// blueprint instructions typed into a rich-text field).
        /// </summary>
        public static string StripHtml(string? input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return "";

            var text = NonContentBlocks.Replace(input, " ");
            text = HtmlComments.Replace(text, " ");
            text = LineBreakTags.Replace(text, "\n");
            text = BlockBoundaryTags.Replace(text, "\n");
            text = AnyTag.Replace(text, "");

            text = WebUtility.HtmlDecode(text);

            text = CssRuleBlocks.Replace(text, " ");
            text = DataUri.Replace(text, "");
            text = InvisibleChars.Replace(text, "");
            text = text.Replace(' ', ' ');

            return NormalizeWhitespace(text);
        }

        /// <summary>
        /// Everything <see cref="StripHtml"/> does, plus the cuts that only make
        /// sense for a mail body: the quoted history, footer boilerplate and
        /// tracking links. What comes back is the part of the message the
        /// sender actually wrote, at full length unless a budget is passed.
        /// </summary>
        /// <param name="maxChars">
        /// Character budget for the result; 0 or less means no truncation,
        /// which is the default.
        /// </param>
        public static string CleanEmailBody(string? input, int maxChars = DefaultEmailBodyBudget)
        {
            var text = StripHtml(input);

            if (text.Length == 0)
                return "";

            text = CutQuotedTrail(text);
            text = LongUrl.Replace(text, "[link]");
            text = DropBoilerplateLines(text);
            text = NormalizeWhitespace(text);

            return Truncate(text, maxChars);
        }

        /// <summary>
        /// Keeps the structure of an HTML email (paragraphs, bold, lists,
        /// links, colours, alignment) so the model can reproduce its
        /// formatting, and drops what carries nothing for it: scripts/styles,
        /// comments, event handlers, class/id attributes, embedded base64
        /// images and invisible characters.
        ///
        /// Typeface and text size go too. They are not formatting the model
        /// should copy: whatever font the example happened to be typed in gets
        /// reproduced on some runs of the generated mail and not others, and
        /// the result renders in two fonts. The app decides the typeface;
        /// the example only decides the shape.
        ///
        /// Plain-text input comes back unchanged apart from trimming.
        /// </summary>
        public static string CleanEmailHtml(string? input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return "";

            if (!LooksLikeHtml(input))
                return input.Trim();

            var html = NonContentBlocks.Replace(input, "");
            html = HtmlComments.Replace(html, "");
            html = EventHandlerAttrs.Replace(html, "");
            html = ClassIdAttrs.Replace(html, "");
            html = DataUri.Replace(html, "");
            html = InvisibleChars.Replace(html, "");
            html = StripTypography(html);
            html = Regex.Replace(html, @">\s+<", "><");

            return html.Trim();
        }

        /// <summary>
        /// Drops the typeface and text-size declarations from inline styles and
        /// the legacy face/size attributes from &lt;font&gt; tags, leaving every
        /// other declaration (colour, weight, alignment, spacing) in place. A
        /// style attribute left with nothing in it is removed rather than kept
        /// empty.
        /// </summary>
        private static string StripTypography(string html)
        {
            html = StyleAttr.Replace(html, match =>
            {
                var quoted = match.Groups[1].Value;
                var quote = quoted[0];
                var declarations = quoted[1..^1];

                var kept = TypographyDeclarations.Replace(declarations, "")
                                                 .Trim(TrimmedFromStyle);

                return kept.Length == 0 ? "" : $" style={quote}{kept}{quote}";
            });

            // face/size are scoped to the <font> tag they sit in, so a size=
            // on an <input> or <hr> elsewhere in the mail is left alone.
            return FontTag.Replace(html, m => FontPresentationAttrs.Replace(m.Value, ""));
        }

        private static readonly char[] TrimmedFromStyle = { ' ', ';', '\t', '\n', '\r' };

        private static readonly Regex StyleAttr = new(
            @"\s+style\s*=\s*(""[^""]*""|'[^']*')",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // font-family / font-size / the font shorthand / line-height, together
        // with the separator in front of them. "font" is tried last and only
        // matches when a ":" follows immediately, so font-weight and font-style
        // fall through untouched.
        private static readonly Regex TypographyDeclarations = new(
            @"(?:^|;)\s*(?:font-family|font-size|line-height|font)\s*:[^;]*",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex FontTag = new(
            @"<font\b[^>]*>",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex FontPresentationAttrs = new(
            @"\s+(?:face|size)\s*=\s*(""[^""]*""|'[^']*'|[^\s>]+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex EventHandlerAttrs = new(
            @"\s+on[a-z]+\s*=\s*(""[^""]*""|'[^']*'|[^\s>]+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ClassIdAttrs = new(
            @"\s+(class|id)\s*=\s*(""[^""]*""|'[^']*'|[^\s>]+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// True when the value looks like markup rather than plain text, so
        /// callers can leave hand-typed text untouched.
        /// </summary>
        public static bool LooksLikeHtml(string? value)
            => !string.IsNullOrWhiteSpace(value)
               && Regex.IsMatch(value, @"<\s*/?\s*[a-z][a-z0-9]*(\s[^>]*)?>", RegexOptions.IgnoreCase);

        // ------------------------------------------------------------------

        // Keeps the text up to the first quoted-history marker. The quote is
        // dropped rather than kept-and-shortened because every message inside it
        // is already its own entry in the transcript.
        private static string CutQuotedTrail(string text)
        {
            var cutAt = text.Length;

            foreach (var marker in QuotedTrailMarkers)
            {
                var match = marker.Match(text);

                // A marker at the very top means the whole body is a quote -
                // there is no new content above it to keep, so leave it alone
                // and let the budget trim it instead.
                if (match.Success && match.Index > 40 && match.Index < cutAt)
                    cutAt = match.Index;
            }

            return cutAt == text.Length ? text : text[..cutAt].TrimEnd();
        }

        private static string DropBoilerplateLines(string text)
        {
            var lines = text.Split('\n');
            var kept = new List<string>(lines.Length);

            foreach (var line in lines)
            {
                if (BoilerplateLine.IsMatch(line))
                    continue;

                if (SeparatorOnlyLine.IsMatch(line))
                    continue;

                kept.Add(line);
            }

            return string.Join("\n", kept);
        }

        private static string NormalizeWhitespace(string text)
        {
            text = text.Replace("\r\n", "\n").Replace('\r', '\n');
            text = Regex.Replace(text, @"[ \t]+", " ");
            text = Regex.Replace(text, @" ?\n ?", "\n");
            text = Regex.Replace(text, @"\n{3,}", "\n\n");

            return text.Trim();
        }

        // Cuts on a line, then a word, so the result never ends mid-token.
        private static string Truncate(string text, int maxChars)
        {
            if (maxChars <= 0 || text.Length <= maxChars)
                return text;

            var slice = text[..maxChars];

            var breakAt = slice.LastIndexOf('\n');
            if (breakAt < maxChars / 2)
                breakAt = slice.LastIndexOf(' ');
            if (breakAt < maxChars / 2)
                breakAt = maxChars;

            return new StringBuilder(slice[..breakAt].TrimEnd())
                .Append("\n[... message truncated ...]")
                .ToString();
        }
    }
}
