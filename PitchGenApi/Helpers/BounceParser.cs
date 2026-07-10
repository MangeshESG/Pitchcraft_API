namespace PitchGenApi.Helpers
{
    using PitchGenApi.Model.DTOs;
    using System.Text.RegularExpressions;

    public static class BounceParser
    {
        public static BounceParseResult Parse(BounceMailInput input)
        {
            var result = new BounceParseResult();

            var fromEmail = input.FromEmail?.ToLower() ?? "";
            var subject = input.Subject?.ToLower() ?? "";
            var body = input.Body ?? "";
            var headers = input.HeadersText ?? "";

            var fullText = body + "\n" + headers;

            bool isBounce =
                fromEmail.Contains("mailer-daemon") ||
                fromEmail.Contains("postmaster") ||
                subject.Contains("delivery status notification") ||
                subject.Contains("undelivered") ||
                subject.Contains("delivery failure") ||
                subject.Contains("mail delivery failed") ||
                subject.Contains("returned mail") ||
                subject.Contains("failure notice") ||
                subject.Contains("message not delivered") ||
                fullText.Contains("Final-Recipient:", StringComparison.OrdinalIgnoreCase) ||
                fullText.Contains("Original-Recipient:", StringComparison.OrdinalIgnoreCase) ||
                fullText.Contains("Diagnostic-Code:", StringComparison.OrdinalIgnoreCase) ||
                fullText.Contains("X-Failed-Recipients:", StringComparison.OrdinalIgnoreCase) ||
                fullText.Contains("Status:", StringComparison.OrdinalIgnoreCase);

            if (!isBounce)
                return result;

            result.IsBounce = true;

            result.OriginalMessageId =
                ExtractRegex(fullText, @"Original-Message-ID:\s*<?([^>\r\n]+)>?") ??
                ExtractRegex(fullText, @"X-Original-Message-ID:\s*<?([^>\r\n]+)>?");

            result.RecipientEmail =
                ExtractRegex(fullText, @"Final-Recipient:\s*rfc822;\s*([^\s\r\n;,]+)") ??
                ExtractRegex(fullText, @"Original-Recipient:\s*rfc822;\s*([^\s\r\n;,]+)") ??
                ExtractRegex(fullText, @"X-Failed-Recipients:\s*([^\s\r\n;,]+)") ??
                ExtractEmailFromText(fullText);

            result.Action =
                ExtractRegex(fullText, @"Action:\s*([^\r\n]+)");

            result.StatusCode =
                ExtractRegex(fullText, @"Status:\s*([^\r\n]+)");

            result.DiagnosticCode =
                ExtractRegex(fullText, @"Diagnostic-Code:\s*([^\r\n]+)") ??
                ExtractRegex(fullText, @"Diagnostic-Code:\s*smtp;\s*([^\r\n]+)");

            result.RemoteServer =
                ExtractRegex(fullText, @"Remote-MTA:\s*dns;\s*([^\r\n]+)");

            result.Reason =
                 ExtractRegex(fullText, @"Address not found\s*([\s\S]*?)(?=LEARN MORE|The response was:|$)") ??
                 ExtractRegex(fullText, @"Your message wasn't delivered to\s+[^\s]+\s+because\s+([\s\S]*?)(?=LEARN MORE|The response was:|$)") ??
                 ExtractRegex(fullText, @"The response was:\s*([\s\S]*?)(?=\r?\n\r?\n|$)") ??
                 result.DiagnosticCode ??
                 ExtractRegex(fullText, @"Reason:\s*([^\r\n]+)") ??
                 input.Subject;

            result.BounceType = GetBounceType(
                result.StatusCode,
                result.DiagnosticCode,
                result.Reason
            );

            return result;
        }

        private static string? ExtractRegex(string input, string pattern)
        {
            if (string.IsNullOrWhiteSpace(input))
                return null;

            var match = Regex.Match(
                input,
                pattern,
                RegexOptions.IgnoreCase | RegexOptions.Multiline);

            return match.Success ? match.Groups[1].Value.Trim() : null;
        }

        private static string? ExtractEmailFromText(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return null;

            var match = Regex.Match(
                input,
                @"[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}",
                RegexOptions.IgnoreCase);

            return match.Success ? match.Value.Trim() : null;
        }

        private static string GetBounceType(
            string? statusCode,
            string? diagnosticCode,
            string? reason)
        {
            var text = $"{statusCode} {diagnosticCode} {reason}".ToLower();

            if (text.Contains("5.1.1") ||
                text.Contains("user unknown") ||
                text.Contains("no such user") ||
                text.Contains("recipient address rejected") ||
                text.Contains("does not exist"))
            {
                return "Invalid Recipient";
            }

            if (text.Contains("mailbox full") ||
                text.Contains("quota exceeded") ||
                text.Contains("5.2.2"))
            {
                return "Mailbox Full";
            }

            if (text.Contains("blocked") ||
                text.Contains("spam") ||
                text.Contains("policy") ||
                text.Contains("5.7.1"))
            {
                return "Spam Block";
            }

            if (text.Contains("4."))
                return "Soft Bounce";

            if (text.Contains("5."))
                return "Hard Bounce";

            return "Unknown Bounce";
        }
    }
}
