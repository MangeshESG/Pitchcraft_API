using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PitchGenApi.Database;
using PitchGenApi.Model;
using Serilog;

namespace PitchGenApi.Services
{
    /// <summary>
    /// Keeps a blueprint's signature in step with the one saved under
    /// Settings → Email signature.
    ///
    /// A blueprint stores the signature's HTML in its placeholder values, so
    /// the prompt can be built from the saved JSON alone. That copy is taken
    /// the moment the signature is picked, which means every later edit in
    /// Settings — a new phone number, a link added to the website line, a
    /// different logo — would never reach the campaigns already using it. The
    /// blueprint therefore also stores which signature it picked
    /// (<see cref="SourceIdKey"/>), and this re-reads that signature's current
    /// HTML on the way into the prompt.
    ///
    /// The stored copy is the fallback, not the source: a blueprint whose
    /// signature was typed by hand, or whose saved signature has since been
    /// deleted, keeps exactly what it holds today.
    /// </summary>
    public static class SignatureResolver
    {
        /// <summary>
        /// Placeholder-values entry naming the saved signature a blueprint
        /// picked. Written by the signature dropdown on the elements page; it
        /// holds the EmailSignatures row id, never any text, and no blueprint
        /// has a {signature_source_id} slot for it to fill.
        /// </summary>
        public const string SourceIdKey = "signature_source_id";

        /// <summary>
        /// The rule appended to a prompt whose signature reached the model.
        ///
        /// Keeping the signature's HTML in the prompt is only half the job: a
        /// writing model asked for a whole email will happily re-type the
        /// sign-off in its own words, and what comes back is the same three
        /// lines with the link, the font and the logo gone. The signature is
        /// the one part of the email that is not being written, so the prompt
        /// says so in as many words.
        /// </summary>
        public const string VerbatimRule =
            "The signature block above is already finished HTML. Reproduce it in "
            + "body_html exactly as it appears - the same tags, links, colours, "
            + "fonts and line breaks, character for character - as the last thing "
            + "in the email. Do not re-type it, re-word it, re-format it, convert "
            + "it to plain text, or drop any of its markup.";

        /// <summary>
        /// The blueprint's signature value, or empty when it has none. The
        /// first signature slot wins; a blueprint with two is not a case worth
        /// inventing a rule for.
        /// </summary>
        public static string FindSignatureValue(IDictionary<string, string>? values)
        {
            if (values == null)
                return "";

            foreach (var pair in values)
            {
                if (!PlaceholderEngine.IsSignatureKey(pair.Key))
                    continue;

                if (string.Equals(pair.Key, SourceIdKey, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!string.IsNullOrWhiteSpace(pair.Value))
                    return pair.Value;
            }

            return "";
        }

        /// <summary>
        /// Replaces every signature value in <paramref name="values"/> with the
        /// current HTML of the signature the blueprint is using. Does nothing
        /// when the blueprint has no signature, when nothing in Settings
        /// matches it, or when the saved signature is empty.
        /// </summary>
        public static async Task ApplySavedSignatureAsync(
            AppDbContext dbContext,
            Dictionary<string, string> values,
            int clientId)
        {
            try
            {
                if (dbContext == null || values == null || clientId <= 0)
                    return;

                // Every signature slot the blueprint holds, because the key is
                // whatever its author typed - "signature", "email_signature"
                // and "signature_block" are all in use. The id entry itself is
                // not a slot, so it is left alone.
                var signatureKeys = values.Keys
                    .Where(key => PlaceholderEngine.IsSignatureKey(key)
                               && !string.Equals(key, SourceIdKey, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (signatureKeys.Count == 0)
                    return;

                var stored = FindSignatureValue(values);
                var hasSourceId =
                    values.TryGetValue(SourceIdKey, out var rawId)
                    && int.TryParse((rawId ?? "").Trim(), out var parsedId)
                    && parsedId > 0;

                var signatureId = hasSourceId ? int.Parse((rawId ?? "").Trim()) : 0;

                EmailSignatures? signature = null;

                if (hasSourceId)
                {
                    signature = await dbContext.EmailSignatures
                        .AsNoTracking()
                        .FirstOrDefaultAsync(s => s.Id == signatureId && s.ClientId == clientId);
                }

                // No id recorded - a blueprint saved before the signature
                // dropdown existed, or one whose signature was typed by hand.
                // Matched on what it reads rather than on its markup, which is
                // the whole point: the stored copy and the saved signature
                // differ by exactly the formatting that went missing. Adding a
                // link to a line does not change the words of that line, so the
                // copy still recognises its original.
                if (signature == null && !string.IsNullOrWhiteSpace(stored))
                {
                    var fingerprint = Fingerprint(stored);

                    if (fingerprint.Length > 0)
                    {
                        var candidates = await dbContext.EmailSignatures
                            .AsNoTracking()
                            .Where(s => s.ClientId == clientId)
                            .ToListAsync();

                        signature = candidates.FirstOrDefault(
                            s => Fingerprint(s.SignatureHtml) == fingerprint);
                    }
                }

                if (signature == null || string.IsNullOrWhiteSpace(signature.SignatureHtml))
                    return;

                foreach (var key in signatureKeys)
                    values[key] = signature.SignatureHtml;
            }
            catch (Exception ex)
            {
                // A signature that could not be refreshed is not worth failing
                // a generation over - the stored copy is still a signature.
                Log.Warning(ex,
                    "Could not refresh the saved signature for client {ClientId}", clientId);
            }
        }

        /// <summary>
        /// What a signature reads as, with its markup, entities and spacing
        /// taken out: the part that survives being re-formatted.
        /// </summary>
        private static string Fingerprint(string? html)
        {
            if (string.IsNullOrWhiteSpace(html))
                return "";

            var text = PromptTextCleaner.StripHtml(html);

            // &nbsp; decodes to a non-breaking space, which is not the
            // space the same signature is typed with elsewhere.
            text = WebUtility.HtmlDecode(text ?? "").Replace('\u00A0', ' ');

            return Whitespace.Replace(text, " ").Trim().ToLowerInvariant();
        }

        private static readonly Regex Whitespace =
            new(@"\s+", RegexOptions.Compiled);
    }
}
