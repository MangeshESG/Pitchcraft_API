using PitchGenApi.Model.DTOs;
using System.Net;
using System.Text.RegularExpressions;

public static class BounceParser
{
    private static readonly string[] BounceSubjectKeywords =
    {
        "delivery status notification",
        "delivery failure",
        "delivery failed",
        "mail delivery failed",
        "message delivery failed",
        "undelivered mail",
        "undeliverable",
        "returned mail",
        "failure notice",
        "message not delivered",
        "address not found",
        "delivery notification",
        "non-delivery report",
        "delivery report",
        "could not be delivered",
        "unable to deliver",
        "mail system error",
        "mail delivery subsystem"
    };

    private static readonly string[] BounceSenderKeywords =
    {
        "mailer-daemon",
        "postmaster",
        "mail delivery subsystem",
        "mail delivery system",
        "mail-daemon",
        "mdaemon",
        "no-reply"
    };

    public static BounceParseResult Parse(BounceMailInput input)
    {
        var result = new BounceParseResult();

        var rawBody = input.Body ?? string.Empty;
        var plainBody = ConvertHtmlToPlainText(rawBody);
        var headers = input.HeadersText ?? string.Empty;
        var subject = input.Subject ?? string.Empty;
        var fromEmail = input.FromEmail ?? string.Empty;

        var fullText = $"{headers}\n{plainBody}";

        result.IsBounce = IsBounceMessage(
            fromEmail,
            subject,
            fullText
        );

        if (!result.IsBounce)
            return result;

        result.OriginalMessageId = FindOriginalMessageId(fullText);

        result.RecipientEmail = FindFailedRecipient(fullText);

        result.Action = FindAction(fullText);

        result.StatusCode = FindEnhancedStatusCode(fullText);

        result.SmtpStatusCode = FindSmtpStatusCode(fullText);

        result.DiagnosticCode = FindDiagnosticCode(fullText);

        result.RemoteServer = FindRemoteServer(fullText);

        result.Reason = FindReason(
            plainBody,
            fullText,
            result.DiagnosticCode,
            subject
        );

        result.BounceType = GetBounceType(
            result.StatusCode,
            result.SmtpStatusCode,
            result.DiagnosticCode,
            result.Reason,
            result.Action
        );

        return result;
    }

    private static bool IsBounceMessage(
        string fromEmail,
        string subject,
        string fullText)
    {
        var from = fromEmail.ToLowerInvariant();
        var subjectText = subject.ToLowerInvariant();
        var text = fullText.ToLowerInvariant();

        if (BounceSenderKeywords.Any(from.Contains))
            return true;

        if (BounceSubjectKeywords.Any(subjectText.Contains))
            return true;

        string[] structuredBounceFields =
        {
            "final-recipient:",
            "original-recipient:",
            "diagnostic-code:",
            "x-failed-recipients:",
            "original-message-id:",
            "remote-mta:",
            "reporting-mta:",
            "arrival-date:",
            "will-retry-until:",
            "action: failed",
            "action: delayed",
            "action: expanded",
            "action: relayed",
            "status:"
        };

        if (structuredBounceFields.Any(text.Contains))
            return true;

        string[] commonBouncePhrases =
        {
            "your message wasn't delivered",
            "your message was not delivered",
            "couldn't be delivered",
            "could not be delivered",
            "unable to deliver",
            "delivery has failed",
            "delivery failure",
            "delivery failed",
            "address not found",
            "recipient address rejected",
            "mailbox unavailable",
            "mailbox does not exist",
            "user unknown",
            "unknown user",
            "no such user",
            "account that you tried to reach does not exist",
            "undeliverable",
            "returned to sender",
            "message blocked",
            "mailbox full",
            "quota exceeded"
        };

        if (commonBouncePhrases.Any(text.Contains))
            return true;

        return Regex.IsMatch(
            fullText,
            @"\b[45]\d{2}[\s\-]+[245]\.\d{1,3}\.\d{1,3}\b",
            RegexOptions.IgnoreCase
        );
    }

    private static string? FindOriginalMessageId(string input)
    {
        string[] patterns =
        {
            @"Original-Message-ID\s*:\s*<?([^>\r\n\s]+)>?",
            @"X-Original-Message-ID\s*:\s*<?([^>\r\n\s]+)>?",
            @"Original-Message-Id\s*:\s*<?([^>\r\n\s]+)>?",
            @"In-Reply-To\s*:\s*<?([^>\r\n\s]+)>?",
            @"References\s*:\s*(?:.*<)?([^<>\s]+@[^<>\s]+)>?\s*$",
            @"Message-ID of original message\s*:\s*<?([^>\r\n\s]+)>?",
            @"Message-Id\s*:\s*<?([^>\r\n\s]+)>?"
        };

        return ExtractFirstMatch(input, patterns);
    }

    private static string? FindFailedRecipient(string input)
    {
        string[] patterns =
        {
            // Standard DSN
            @"Final-Recipient\s*:\s*rfc822\s*;\s*<?([^>\s\r\n;,]+)>?",
            @"Original-Recipient\s*:\s*rfc822\s*;\s*<?([^>\s\r\n;,]+)>?",
            @"X-Failed-Recipients\s*:\s*<?([^>\s\r\n;,]+)>?",

            // Gmail
            @"message\s+(?:wasn't|was not)\s+delivered\s+to\s+<?([A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,})>?",
            @"Your message to\s+<?([A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,})>?\s+couldn't be delivered",

            // Outlook / Exchange
            @"Your message couldn't be delivered to\s+<?([A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,})>?",
            @"Delivery has failed to these recipients or groups\s*:\s*<?([A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,})>?",
            @"The following recipient(?:s)? could not be reached\s*:\s*<?([A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,})>?",
            @"Remote Server returned.*?for\s+<?([A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,})>?",

            // Generic
            @"Recipient\s*:\s*<?([A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,})>?",
            @"Failed Recipient\s*:\s*<?([A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,})>?",
            @"Undelivered to\s*:\s*<?([A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,})>?",
            @"Could not deliver message to\s+<?([A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,})>?"
        };

        return ExtractFirstMatch(input, patterns) ??
               ExtractFirstEmail(input);
    }

    private static string? FindAction(string input)
    {
        var action = ExtractFirstMatch(
            input,
            new[]
            {
                @"Action\s*:\s*(failed|delayed|delivered|relayed|expanded)",
                @"Delivery-Status\s*:\s*(failed|delayed)"
            }
        );

        if (!string.IsNullOrWhiteSpace(action))
            return action.ToLowerInvariant();

        var smtpCode = FindSmtpStatusCode(input);

        if (smtpCode?.StartsWith("5") == true)
            return "failed";

        if (smtpCode?.StartsWith("4") == true)
            return "delayed";

        return null;
    }

    private static string? FindEnhancedStatusCode(string input)
    {
        string[] patterns =
        {
            @"Status\s*:\s*([245]\.\d{1,3}\.\d{1,3})",
            @"\b[45]\d{2}[\s\-]+([245]\.\d{1,3}\.\d{1,3})\b",
            @"\b([245]\.\d{1,3}\.\d{1,3})\b",
            @"smtp\s*;\s*[45]\d{2}[\s\-]+([245]\.\d{1,3}\.\d{1,3})"
        };

        return ExtractFirstMatch(input, patterns);
    }

    private static string? FindSmtpStatusCode(string input)
    {
        string[] patterns =
        {
            @"Diagnostic-Code\s*:\s*(?:smtp\s*;\s*)?([45]\d{2})\b",
            @"The response was\s*:\s*([45]\d{2})\b",
            @"Remote Server returned\s*['""]?([45]\d{2})\b",
            @"Server response\s*:\s*([45]\d{2})\b",
            @"SMTP error\s*:\s*([45]\d{2})\b",
            @"\b([45]\d{2})[\s\-]+[245]\.\d{1,3}\.\d{1,3}\b"
        };

        return ExtractFirstMatch(input, patterns);
    }

    private static string? FindDiagnosticCode(string input)
    {
        string[] patterns =
        {
            @"Diagnostic-Code\s*:\s*(?:smtp\s*;\s*)?([^\r\n]+)",

            @"The response was\s*:\s*([\s\S]*?)(?=\r?\n\s*\r?\n|$)",

            @"Remote Server returned\s*['""]?([\s\S]*?)(?=['""]?\r?\n|$)",

            @"Server response\s*:\s*([^\r\n]+)",

            @"SMTP error(?: from remote mail server)?\s*:\s*([^\r\n]+)",

            @"Reason\s*:\s*([^\r\n]+)",

            @"\b([45]\d{2}[\s\-]+[245]\.\d{1,3}\.\d{1,3}[^\r\n]*)"
        };

        return ExtractFirstMatch(input, patterns);
    }

    private static string? FindRemoteServer(string input)
    {
        string[] patterns =
        {
            @"Remote-MTA\s*:\s*(?:dns\s*;\s*)?([^\r\n]+)",
            @"Reporting-MTA\s*:\s*(?:dns\s*;\s*)?([^\r\n]+)",
            @"Received-From-MTA\s*:\s*(?:dns\s*;\s*)?([^\r\n]+)",
            @"Generating server\s*:\s*([^\r\n]+)",
            @"Remote Server returned.*?from\s+([A-Z0-9.\-]+)",
            @"host\s+([A-Z0-9.\-]+)\s+said\s*:"
        };

        return ExtractFirstMatch(input, patterns);
    }

    private static string FindReason(
        string plainBody,
        string fullText,
        string? diagnosticCode,
        string subject)
    {
        string[] patterns =
        {
            // Gmail
            @"Your message (?:wasn't|was not) delivered to\s+\S+\s+because\s+([\s\S]*?)(?=LEARN MORE|The response was|$)",

            // Outlook / Exchange
            @"Your message couldn't be delivered to\s+\S+\s+because\s+([\s\S]*?)(?=Diagnostic information|Original message|$)",
            @"The following organization rejected your message\s*:\s*([\s\S]*?)(?=Diagnostic information|$)",
            @"Delivery has failed.*?\.\s*([\s\S]*?)(?=Diagnostic information|Original message|$)",

            // Generic
            @"Reason\s*:\s*([^\r\n]+)",
            @"Error\s*:\s*([^\r\n]+)",
            @"Explanation\s*:\s*([^\r\n]+)",
            @"Delivery failed because\s+([^\r\n]+)",
            @"Unable to deliver because\s+([^\r\n]+)",
            @"The mail system\s*:\s*([\s\S]*?)(?=\r?\n\s*\r?\n|$)"
        };

        var reason = ExtractFirstMatch(plainBody, patterns);

        if (!string.IsNullOrWhiteSpace(reason))
            return CleanValue(reason);

        if (!string.IsNullOrWhiteSpace(diagnosticCode))
            return CleanValue(diagnosticCode);

        var statusLine = ExtractFirstMatch(
            fullText,
            new[]
            {
                @"\b([45]\d{2}[\s\-]+[245]\.\d{1,3}\.\d{1,3}[^\r\n]*)",
                @"\b([245]\.\d{1,3}\.\d{1,3}[^\r\n]*)"
            }
        );

        return CleanValue(statusLine ?? subject);
    }

    private static string GetBounceType(
        string? enhancedStatus,
        string? smtpStatus,
        string? diagnostic,
        string? reason,
        string? action)
    {
        var text =
            $"{enhancedStatus} {smtpStatus} {diagnostic} {reason} {action}"
                .ToLowerInvariant();

        // Invalid recipient
        if (ContainsAny(text,
            "5.1.0",
            "5.1.1",
            "5.1.3",
            "5.1.4",
            "5.1.6",
            "user unknown",
            "unknown user",
            "no such user",
            "nosuchuser",
            "recipient not found",
            "recipient address rejected",
            "invalid recipient",
            "address not found",
            "address couldn't be found",
            "address could not be found",
            "account does not exist",
            "mailbox does not exist"))
        {
            return "Invalid Recipient";
        }

        // Mailbox full
        if (ContainsAny(text,
            "5.2.2",
            "4.2.2",
            "mailbox full",
            "mailbox is full",
            "quota exceeded",
            "over quota",
            "storage quota",
            "insufficient storage"))
        {
            return "Mailbox Full";
        }

        // Mailbox disabled/unavailable
        if (ContainsAny(text,
            "5.2.1",
            "mailbox disabled",
            "mailbox unavailable",
            "account disabled",
            "inactive mailbox",
            "recipient unavailable"))
        {
            return "Mailbox Unavailable";
        }

        // Spam or policy block
        if (ContainsAny(text,
            "5.7.0",
            "5.7.1",
            "5.7.26",
            "spam",
            "blocked",
            "blacklist",
            "blacklisted",
            "policy rejection",
            "policy violation",
            "message rejected",
            "authentication required",
            "unauthenticated email",
            "dmarc",
            "spf",
            "dkim"))
        {
            return "Spam Or Policy Block";
        }

        // Message too large
        if (ContainsAny(text,
            "5.2.3",
            "5.3.4",
            "message too large",
            "message size exceeds",
            "maximum message size",
            "exceeded message size"))
        {
            return "Message Too Large";
        }

        // Domain/DNS error
        if (ContainsAny(text,
            "5.1.2",
            "domain not found",
            "host not found",
            "no mx record",
            "dns error",
            "dns failure",
            "unrouteable address",
            "domain does not exist"))
        {
            return "Invalid Domain";
        }

        // Routing / loop
        if (ContainsAny(text,
            "5.4.0",
            "5.4.1",
            "5.4.4",
            "5.4.6",
            "routing loop",
            "mail loop",
            "too many hops",
            "unable to route"))
        {
            return "Routing Error";
        }

        // Temporary server/network failure
        if (ContainsAny(text,
            "4.3.0",
            "4.4.0",
            "4.4.1",
            "4.4.2",
            "4.4.7",
            "temporarily unavailable",
            "temporary failure",
            "connection timed out",
            "connection refused",
            "network error",
            "try again later",
            "server busy",
            "service unavailable",
            "greylisted"))
        {
            return "Soft Bounce";
        }

        if (enhancedStatus?.StartsWith("4.") == true ||
            smtpStatus?.StartsWith("4") == true ||
            action?.Equals("delayed", StringComparison.OrdinalIgnoreCase) == true)
        {
            return "Soft Bounce";
        }

        if (enhancedStatus?.StartsWith("5.") == true ||
            smtpStatus?.StartsWith("5") == true ||
            action?.Equals("failed", StringComparison.OrdinalIgnoreCase) == true)
        {
            return "Hard Bounce";
        }

        return "Unknown Bounce";
    }

    private static string? ExtractFirstMatch(
        string input,
        IEnumerable<string> patterns)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        foreach (var pattern in patterns)
        {
            var match = Regex.Match(
                input,
                pattern,
                RegexOptions.IgnoreCase |
                RegexOptions.Multiline |
                RegexOptions.Singleline
            );

            if (match.Success && match.Groups.Count > 1)
            {
                var value = CleanValue(match.Groups[1].Value);

                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }
        }

        return null;
    }

    private static string? ExtractFirstEmail(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        var match = Regex.Match(
            input,
            @"[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}",
            RegexOptions.IgnoreCase
        );

        return match.Success
            ? match.Value.Trim('<', '>', ' ', '\r', '\n')
            : null;
    }

    private static bool ContainsAny(
        string input,
        params string[] values)
    {
        return values.Any(input.Contains);
    }

    private static string ConvertHtmlToPlainText(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return string.Empty;

        var text = Regex.Replace(
            html,
            @"<(script|style)\b[^>]*>[\s\S]*?</\1>",
            " ",
            RegexOptions.IgnoreCase
        );

        text = Regex.Replace(
            text,
            @"<br\s*/?>|</p>|</div>|</tr>|</td>|</li>|</h[1-6]>",
            "\n",
            RegexOptions.IgnoreCase
        );

        text = Regex.Replace(text, @"<[^>]+>", " ");

        text = WebUtility.HtmlDecode(text);

        text = Regex.Replace(text, @"[ \t]+", " ");
        text = Regex.Replace(text, @"\r?\n\s*\r?\n+", "\n");

        return text.Trim();
    }

    private static string CleanValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        value = WebUtility.HtmlDecode(value);

        value = Regex.Replace(value, @"<[^>]+>", " ");
        value = Regex.Replace(value, @"\s+", " ");

        return value
            .Trim()
            .Trim('<', '>', '\'', '"', ';', ',');
    }
}