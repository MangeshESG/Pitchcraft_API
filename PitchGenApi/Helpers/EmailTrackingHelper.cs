using HtmlAgilityPack;
using PitchGenApi.Model;
using PitchGenApi.Models;
using System.Net;
using System.Text.RegularExpressions;

public static class EmailTrackingHelper
{
    public static string GetPixelTag(string trackingId)
    {
        string B64(string s) =>
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(s ?? ""));

        var url =
           $"https://app.pitchkraft.ai/track/open" +
           $"?trackingId={B64(trackingId)}";

        return
            $"<img src=\"{url}\" width=\"1\" height=\"1\" style=\"display:none;max-height:0;overflow:hidden;\" alt=\"\" />";
    }

    public static string InjectClickTracking(string htmlBody, string trackingId)
    {
        string EncodeBase64(string plainText)
        {
            if (string.IsNullOrEmpty(plainText)) return "";
            var plainBytes = System.Text.Encoding.UTF8.GetBytes(plainText);
            return Convert.ToBase64String(plainBytes);
        }

        string B64Int(int? num) =>
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes((num ?? 0).ToString()));

        var doc = new HtmlAgilityPack.HtmlDocument();
        doc.LoadHtml(htmlBody);

        var links = doc.DocumentNode.SelectNodes("//a[@href]");
        if (links == null) return htmlBody;

        foreach (var link in links)
        {
            var originalUrl = link.GetAttributeValue("href", "");
            if (string.IsNullOrWhiteSpace(originalUrl)) continue;

            var encodedUrl = EncodeBase64(originalUrl);
            
            var encodedTrackingId = EncodeBase64(trackingId);

            var trackingUrl = $"https://app.pitchkraft.ai/track/click?trackingId={encodedTrackingId}&url={encodedUrl}";

            link.SetAttributeValue("href", trackingUrl);
        }

        return doc.DocumentNode.OuterHtml;
    }
    public static string InjectinboxTracking(string body, string trackingId)
    {
        if (string.IsNullOrEmpty(body)) return body;

        var trackingTag = $"<div style='display:none'>TRACKING_ID:{trackingId}</div>";

        // HTML body hai toh append kar
        if (body.Contains("</body>"))
        {
            return body.Replace("</body>", trackingTag + "</body>");
        }

        return body + trackingTag;
    }

    // ✅ Extract tracking from email body
    public static Guid? ExtractinboxTrackingId(string body)
    {
        if (string.IsNullOrEmpty(body))
            return null;

        var match = Regex.Match(body, @"TRACKING_ID:([a-zA-Z0-9\-]+)");

        if (!match.Success)
            return null;

        var value = match.Groups[1].Value;

        // ✅ SAFE parse
        if (Guid.TryParse(value, out var guid))
            return guid;

        return null;
    }

    public static string GetBrowserName(string userAgent)
    {
        userAgent = userAgent.ToLower();

        if (userAgent.Contains("edg/")) return "Edge";
        if (userAgent.Contains("chrome/") && !userAgent.Contains("edg/")) return "Chrome";
        if (userAgent.Contains("firefox/")) return "Firefox";
        if (userAgent.Contains("safari/") && !userAgent.Contains("chrome/")) return "Safari";
        if (userAgent.Contains("opera") || userAgent.Contains("opr/")) return "Opera";

        return "Unknown";
    }
}
