using HtmlAgilityPack;
using PitchGenApi.Model;
using PitchGenApi.Models;
using System.Net;

public static class EmailTrackingHelper
{
    public static string GetPixelTag(
     string email, int clientId, int? dataFileId, int? segmentId,
     int contactId, string fullName, string location, string company,
     string website, string linkedin, string jobTitle, string trackingId)
    {
        string B64(string s) =>
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(s ?? ""));

        string B64Int(int? num) =>
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes((num ?? 0).ToString()));

        return $@"<img src=""https://app.pitchkraft.ai/track/open?
                email={B64(email)}
                &clientId={B64Int(clientId)}
                &SegmentId={B64Int(segmentId)}
                &DataFileId={B64Int(dataFileId)}
                &contactId={B64Int(contactId)}
                &FullName={B64(fullName)}
                &Location={B64(location)}
                &Company={B64(company)}
                &Website={B64(website)}
                &linkedin_URL={B64(linkedin)}
                &JobTitle={B64(jobTitle)}
                &trackingId={B64(trackingId)}""
                width=""1"" height=""1"" style=""display:none;max-height:0;overflow:hidden;"" alt="""" />";
    }



    public static string InjectClickTracking(string email, string htmlBody, int clientId, int contactId, int? DataFileId, int? SegmentId,
      string fullName, string location, string company, string website, string linkedin, string jobtitle, string trackingId)
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

            var encodedEmail = EncodeBase64(email);
            var encodedUrl = EncodeBase64(originalUrl);
            var encodedName = EncodeBase64(fullName ?? "");
            var encodedLocation = EncodeBase64(location ?? "");
            var encodedCompany = EncodeBase64(company ?? "");
            var encodedWeb = EncodeBase64(website ?? "");
            var encodedLinkedin = EncodeBase64(linkedin ?? "");
            var encodedJob = EncodeBase64(jobtitle ?? "");
            var encodedTrackingId = EncodeBase64(trackingId);
            var encodedclientId = B64Int(clientId);
            var encodedcontactId = B64Int(contactId);
            var encodedDataFileId = B64Int(DataFileId);
            var encodedSegmentId = B64Int(SegmentId);

            var trackingUrl = $"https://app.pitchkraft.ai/track/click?trackingId={encodedTrackingId}&email={encodedEmail}&url={encodedUrl}&clientId={encodedclientId}&contactId={encodedcontactId}&DataFileId={encodedDataFileId}&SegmentId={encodedSegmentId}&FullName={encodedName}&Location={encodedLocation}&Company={encodedCompany}&Website={encodedWeb}&linkedin_URL={encodedLinkedin}&JobTitle={encodedJob}";

            link.SetAttributeValue("href", trackingUrl);
        }

        return doc.DocumentNode.OuterHtml;
    }
}
