using HtmlAgilityPack;
using System.Net;

public static class EmailTrackingHelper
{
    public static string GetPixelTag(string email, int clientId, string zohoViewName, string fullName, string location, string company, string website, string linkedin, string jobtitle, string trackingId)
    {
        string encodedEmail = WebUtility.UrlEncode(email);
        string encodedView = WebUtility.UrlEncode(zohoViewName ?? "");
        string encodedName = WebUtility.UrlEncode(fullName ?? "");
        string encodedLocation = WebUtility.UrlEncode(location ?? "");
        string encodedCompany = WebUtility.UrlEncode(company ?? "");
        string encodedWeb = WebUtility.UrlEncode(website ?? "");
        string encodedLinkedin = WebUtility.UrlEncode(linkedin ?? "");
        string encodedJob = WebUtility.UrlEncode(jobtitle ?? "");
        string encodedTrackingId = WebUtility.UrlEncode(trackingId);


        return $"<img src=\"https://pitch.dataji.co/track/open?email={encodedEmail}&clientId={clientId}&zohoViewName={encodedView}&FullName={encodedName}&Location={encodedLocation}&Company={encodedCompany}&Website={encodedWeb}&linkedin_URL={encodedLinkedin}&JobTitle={encodedJob}&trackingId={encodedTrackingId}\" width=\"1\" height=\"1\" style=\"display:none; max-height:0; overflow:hidden;\" alt=\"\" />";
    }

    public static string InjectClickTracking(string email, string htmlBody, int clientId, string zohoViewName, string fullName, string location, string company, string website, string linkedin, string jobtitle, string trackingId)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(htmlBody);

        var links = doc.DocumentNode.SelectNodes("//a[@href]");
        if (links == null) return htmlBody;

        foreach (var link in links)
        {
            var originalUrl = link.GetAttributeValue("href", "");
            if (string.IsNullOrWhiteSpace(originalUrl)) continue;

            var encodedEmail = WebUtility.UrlEncode(email);
            var encodedUrl = WebUtility.UrlEncode(originalUrl);
            var encodedView = WebUtility.UrlEncode(zohoViewName ?? "");
            var encodedName = WebUtility.UrlEncode(fullName ?? "");
            var encodedLocation = WebUtility.UrlEncode(location ?? "");
            var encodedCompany = WebUtility.UrlEncode(company ?? "");
            var encodedWeb = WebUtility.UrlEncode(website ?? "");
            var encodedLinkedin = WebUtility.UrlEncode(linkedin ?? "");
            var encodedJob = WebUtility.UrlEncode(jobtitle ?? "");
            var encodedTrackingId = WebUtility.UrlEncode(trackingId);

            var trackingUrl = $"https://pitch.dataji.co/track/click?trackingId={encodedTrackingId}&email={encodedEmail}&url={encodedUrl}&clientId={clientId}&zohoViewName={encodedView}&FullName={encodedName}&Location={encodedLocation}&Company={encodedCompany}&Website={encodedWeb}&linkedin_URL={encodedLinkedin}&JobTitle={encodedJob}";
            link.SetAttributeValue("href", trackingUrl);
        }

        return doc.DocumentNode.OuterHtml;
    }
}
