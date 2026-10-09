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

        // ---- Word / Outlook markup -----------------------------------------
        // Anything pasted out of Word brings these along. They carry no content
        // a reader or a model needs, and <o:p> in particular is how Word marks
        // a paragraph - left in, it is the thing the generator learns to copy.
        private static readonly Regex OfficeXmlIslands = new(
            @"<xml\b[^>]*>.*?</xml\s*>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        // <o:p>, </o:p>, <w:WordDocument>, <v:shape>, <m:oMath>, <st1:place> ...
        private static readonly Regex OfficeNamespaceTags = new(
            @"</?(?:o|w|v|m|x|st\d)\s*:[^>]*>",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // mso-* declarations inside a style attribute. Only Word reads them,
        // and they are part of why a mail spaces itself differently in Outlook.
        private static readonly Regex MsoDeclarations = new(
            @"(?:^|;)\s*mso-[a-z-]+\s*:[^;]*",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // A paragraph or div holding nothing but blank space - Word's way of
        // drawing a gap. An <img> or <a> inside is content, so neither is
        // listed here and a block containing one is never dropped.
        private static readonly Regex EmptyBlocks = new(
            @"<(p|div)\b[^>]*>(?:\s|&nbsp;|&#160;| |<br\s*/?>"
            + @"|<(?:span|font|em|strong|b|i|u|o:p)\b[^>]*>"
            + @"|</(?:span|font|em|strong|b|i|u|o:p)\s*>)*</\1\s*>",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

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
        /// Word's own markup goes too. An example email pasted out of Word or
        /// Outlook carries &lt;o:p&gt; paragraph markers, mso- declarations and
        /// paragraphs holding nothing but &amp;nbsp; to draw a blank line. The
        /// model copies the shape of the example, so left in they are taught as
        /// formatting - and they are the kind that renders as a differently
        /// sized gap in every mail client. The paragraphs survive; only Word's
        /// way of spacing them does not.
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
            html = OfficeXmlIslands.Replace(html, "");
            html = OfficeNamespaceTags.Replace(html, "");
            html = EventHandlerAttrs.Replace(html, "");
            html = ClassIdAttrs.Replace(html, "");
            html = DataUri.Replace(html, "");
            html = InvisibleChars.Replace(html, "");
            html = StripTypography(html);

            // After the Office tags have gone, so that Word's
            // <p><o:p>&nbsp;</o:p></p> spacer is by then an empty paragraph.
            html = RemoveEmptyBlocks(html);
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
            html = RewriteStyleAttributes(
                html,
                declarations => MsoDeclarations.Replace(
                    TypographyDeclarations.Replace(declarations, ""), ""));

            // face/size are scoped to the <font> tag they sit in, so a size=
            // on an <input> or <hr> elsewhere in the mail is left alone.
            return FontTag.Replace(html, m => FontPresentationAttrs.Replace(m.Value, ""));
        }

        /// <summary>
        /// Makes a generated email render the same in the Kraft editor, in
        /// Gmail and in Outlook.
        ///
        /// The example output email is the one input whose HTML is kept, so
        /// that the model reproduces its formatting. When that example was
        /// pasted out of Word, what the model reproduces includes Word's way
        /// of spacing paragraphs: &lt;o:p&gt; markers and paragraphs holding
        /// nothing but &amp;nbsp;. A browser renders such a spacer as a blank
        /// line plus two default paragraph margins; Outlook renders it with
        /// Word's engine and Word's defaults; so one email arrives with a
        /// different gap in each. <see cref="CleanEmailHtml"/> keeps the
        /// pattern out of the prompt, and this keeps it out of the answer -
        /// whether the model learned it from the example or invented it.
        ///
        /// The spacers go, and every paragraph is given an explicit bottom
        /// margin, so the spacing is declared by the email rather than
        /// inherited from whatever default the reading client applies. A
        /// paragraph that already sets a margin of its own - a signature
        /// block, say - is left exactly as it is, and nothing else about the
        /// formatting is touched: bold, lists, links, colours, alignment and
        /// the paragraph structure the example taught all survive.
        ///
        /// Plain-text input comes back unchanged apart from trimming.
        /// </summary>
        public static string NormalizeEmailHtml(string? input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return "";

            if (!LooksLikeHtml(input))
                return input.Trim();

            var html = OfficeXmlIslands.Replace(input, "");
            html = OfficeNamespaceTags.Replace(html, "");
            html = InvisibleChars.Replace(html, "");
            html = RewriteStyleAttributes(html, d => MsoDeclarations.Replace(d, ""));
            html = RemoveEmptyBlocks(html);
            html = EnsureParagraphSpacing(html);

            return html.Trim();
        }

        /// <summary>
        /// Runs <paramref name="rewrite"/> over the declarations of every style
        /// attribute in the markup. A style left with nothing in it is removed
        /// rather than kept empty.
        /// </summary>
        private static string RewriteStyleAttributes(string html, Func<string, string> rewrite)
            => StyleAttr.Replace(html, match =>
            {
                var quoted = match.Groups[1].Value;
                var quote = quoted[0];

                var kept = rewrite(quoted[1..^1]).Trim(TrimmedFromStyle);

                return kept.Length == 0 ? "" : $" style={quote}{kept}{quote}";
            });

        /// <summary>
        /// Drops the blank-space-only blocks, repeatedly: a wrapper only turns
        /// empty once what it wrapped has gone, so Word's trailing
        /// &lt;div&gt;&lt;div&gt;&lt;span&gt;&lt;/span&gt;&lt;/div&gt;&lt;/div&gt;
        /// takes three passes. The loop stops as soon as a pass changes
        /// nothing, and the cap is there so no input can spin it.
        /// </summary>
        private static string RemoveEmptyBlocks(string html)
        {
            for (var pass = 0; pass < 8; pass++)
            {
                var next = EmptyBlocks.Replace(html, "");

                if (next.Length == html.Length)
                    return next;

                html = next;
            }

            return html;
        }

        /// <summary>
        /// Gives every paragraph that does not already declare a margin an
        /// explicit bottom one. Browsers default to 1em above and below, which
        /// collapse to the same 16px between two paragraphs, so the editor
        /// looks exactly as it did; Outlook, which has no such default, now
        /// agrees with it.
        /// </summary>
        private static string EnsureParagraphSpacing(string html)
            => ParagraphOpenTag.Replace(html, match =>
            {
                var attributes = match.Groups[1].Value;

                // <p /> holds no content to space.
                if (attributes.TrimEnd().EndsWith("/", StringComparison.Ordinal))
                    return match.Value;

                var style = StyleAttr.Match(attributes);

                if (!style.Success)
                    return $"<p{attributes} style=\"{ParagraphSpacing}\">";

                var quoted = style.Groups[1].Value;
                var declarations = quoted[1..^1];

                // The email asked for its own spacing; leave it alone.
                if (MarginDeclaration.IsMatch(declarations))
                    return match.Value;

                var quote = quoted[0];
                var kept = declarations.Trim(TrimmedFromStyle);
                var merged = kept.Length == 0 ? ParagraphSpacing : $"{kept};{ParagraphSpacing}";

                return $"<p{attributes[..style.Index]} style={quote}{merged}{quote}"
                     + $"{attributes[(style.Index + style.Length)..]}>";
            });

        // One declared gap between paragraphs, in the unit the rest of the app
        // uses. It is what a browser already does by default, so making it
        // explicit changes nothing on screen and everything in Outlook.
        private const string ParagraphSpacing = "margin:0 0 16px 0";

        private static readonly Regex ParagraphOpenTag = new(
            @"<p\b([^>]*)>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Any margin declaration at all, shorthand or side-specific, so a
        // paragraph that sets only margin-left never has it reset by ours.
        private static readonly Regex MarginDeclaration = new(
            @"(?:^|;)\s*margin[a-z-]*\s*:", RegexOptions.IgnoreCase | RegexOptions.Compiled);

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
