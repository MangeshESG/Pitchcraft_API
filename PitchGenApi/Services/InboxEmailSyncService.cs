using MailKit.Net.Imap;
using MailKit.Search;
using MailKit;
using PitchGenApi.Database;
using PitchGenApi.Interfaces;
using PitchGenApi.Model;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;
using System.Net.Http.Headers;
using System.Text;

public class InboxEmailSyncService : IInboxEmailSyncService
{
    private readonly AppDbContext _context;
    private readonly EmailSendingHelper _emailSending;

    public InboxEmailSyncService(AppDbContext context, EmailSendingHelper emailSending)
    {
        _context = context;
        _emailSending = emailSending;
    }

    public async Task SyncEmailsAsync(Inboxcredentials setting)
    {
        Console.WriteLine($"\n🚀 Starting sync for: {setting.Username}");

        using var client = new ImapClient();

        await client.ConnectAsync(setting.Host, setting.Port, true);
        Console.WriteLine("🔌 Connected to IMAP");

        await client.AuthenticateAsync(setting.Username, setting.Password);
        Console.WriteLine("🔐 Authenticated");

        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadOnly);
        Console.WriteLine("📂 Inbox opened");

        var uids = await inbox.SearchAsync(SearchQuery.All);

        Console.WriteLine($"📊 Total UIDs: {uids.Count}");
        Console.WriteLine($"📌 LastUid: {setting.LastUid}");

        var newUids = uids
            .Where(uid => uid.Id > setting.LastUid)
            .OrderBy(uid => uid.Id)
            .ToList();

        Console.WriteLine($"📩 New Emails: {newUids.Count}");

        long maxUid = setting.LastUid;

        foreach (var uid in newUids)
        {
            Console.WriteLine($"\n📨 Processing UID: {uid.Id}");

            var msg = await inbox.GetMessageAsync(uid);

            Console.WriteLine($"📧 Subject: {msg.Subject}");

            // 🔥 PROVIDER DETECTION START
            string provider = "Unknown";

            // 1. DKIM (best)
            var dkim = msg.Headers["DKIM-Signature"];
            if (!string.IsNullOrEmpty(dkim))
            {
                if (dkim.Contains("google.com")) provider = "Gmail";
                else if (dkim.Contains("outlook.com") || dkim.Contains("microsoft.com")) provider = "Outlook";
            }

            // 2. Received headers
            if (provider == "Unknown")
            {
                var receivedHeaders = msg.Headers.Where(h => h.Field == "Received");

                foreach (var h in receivedHeaders)
                {
                    var val = h.Value.ToLower();

                    if (val.Contains("google.com"))
                    {
                        provider = "Gmail";
                        break;
                    }
                    else if (val.Contains("outlook.com") || val.Contains("microsoft.com"))
                    {
                        provider = "Outlook";
                        break;
                    }
                }
            }

            // 3. Mailer
            if (provider == "Unknown")
            {
                var mailer = msg.Headers["X-Mailer"] ?? msg.Headers["User-Agent"];

                if (!string.IsNullOrEmpty(mailer))
                {
                    if (mailer.Contains("Outlook")) provider = "Outlook";
                    else if (mailer.Contains("Apple Mail")) provider = "Apple";
                    else if (mailer.Contains("Gmail")) provider = "Gmail";
                }
            }

            // 4. Fallback (email)
            if (provider == "Unknown")
            {
                var fromEmail = msg.From.Mailboxes.FirstOrDefault()?.Address?.ToLower();

                if (!string.IsNullOrEmpty(fromEmail))
                {
                    if (fromEmail.Contains("gmail.com")) provider = "Gmail";
                    else if (fromEmail.Contains("outlook.com") || fromEmail.Contains("hotmail.com")) provider = "Outlook";
                    else if (fromEmail.Contains("icloud.com")) provider = "Apple";
                }
            }

            Console.WriteLine($"📡 Provider: {provider}");
            string rawBody = "";

            if (provider == "Gmail")
            {
               rawBody = msg.HtmlBody ?? msg.TextBody ?? "";
            }
            else
            {
                 rawBody = msg.TextBody ?? msg.HtmlBody ?? "";
            }
            // 🔥 PROVIDER DETECTION END

            // 🔥 TRACKING ID
            var trackingId = EmailTrackingHelper.ExtractinboxTrackingId(rawBody);
            Console.WriteLine($"🎯 TrackingId: {trackingId}");

            // 🔥 CLEAN BODY
            var body = Regex.Replace(rawBody, @"TRACKING_ID:[0-9a-fA-F\-]{36}", "");
            body = Regex.Replace(body, @"(?m)^>\s?", "");
            body = body.Trim();

            var rawInReplyTo = msg.Headers["In-Reply-To"];
            var rawReferences = msg.Headers["References"];
            EmailLog? sent = null;

            // 🔥 MATCHING LOGIC (same as yours)
            if (trackingId != null)
            {
                sent = await _context.EmailLogs
                    .FirstOrDefaultAsync(x => x.TrackingId == trackingId);

                if (sent != null)
                    Console.WriteLine("🔥 Matched via TrackingId");
            }

            if (sent == null && !string.IsNullOrEmpty(rawInReplyTo))
            {
                sent = await _context.EmailLogs
                    .FirstOrDefaultAsync(x => x.MessageId == rawInReplyTo);

                if (sent != null)
                    Console.WriteLine("✅ Matched via InReplyTo");
            }

            if (sent == null && !string.IsNullOrEmpty(rawReferences))
            {
                var refs = rawReferences.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                foreach (var refId in refs)
                {
                    sent = await _context.EmailLogs
                        .FirstOrDefaultAsync(x => x.MessageId == refId);

                    if (sent != null)
                    {
                        Console.WriteLine("✅ Matched via References");
                        break;
                    }
                }
            }

            if (sent == null)
            {
                var fromEmail = msg.From.Mailboxes.FirstOrDefault()?.Address?.ToLower();

                sent = await _context.EmailLogs
                    .Where(x => x.ToEmail.ToLower() == fromEmail)
                    .OrderByDescending(x => x.SentAt)
                    .FirstOrDefaultAsync();

                if (sent != null)
                    Console.WriteLine("✅ Matched via fallback email");
            }

            if (sent == null)
            {
                Console.WriteLine("❌ No match → Skipped");
                continue;
            }

            bool exists = await _context.EmailReplies
                .AnyAsync(x => x.MessageId == msg.MessageId);

            if (exists)
            {
                Console.WriteLine("⚠️ Already exists → Skipped");
                continue;
            }

            _context.EmailReplies.Add(new EmailReplies
            {
                ClientId = sent.ClientId,
                ContactId = sent.ContactId,
                CampaignId = sent.CampaignId,
                MessageId = msg.MessageId,
                InReplyTo = rawInReplyTo,
                FromEmail = msg.From.ToString(),
                Subject = msg.Subject,
                Body = body,
                TrackingId = trackingId ?? Guid.Empty,
                Date = msg.Date.UtcDateTime
            });

            Console.WriteLine("💾 Saved");

            if (uid.Id > maxUid)
                maxUid = uid.Id;
        }

        if (maxUid > setting.LastUid)
        {
            setting.LastUid = maxUid;
            _context.Inboxcredentials.Update(setting);
            Console.WriteLine($"📌 LastUid Updated → {maxUid}");
        }

        await _context.SaveChangesAsync();
        Console.WriteLine("✅ DB Saved");

        await client.DisconnectAsync(true);
        Console.WriteLine($"🔌 Disconnected: {setting.Username}");
    }


    public async Task SyncGmailInboxAsync(EmailOAuthTokens tokenData)
    {
        Console.WriteLine($"🚀 Sync Start: {tokenData.Email}");

        // 🔥 Step 1: Token refresh
        tokenData = await _emailSending.GetValidGmailTokenAsync(tokenData.Id);

        if (tokenData == null)
        {
            Console.WriteLine("❌ Token invalid");
            return;
        }

        var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokenData.AccessToken);

        // 🔥 Step 2: Last Sync Time (with buffer)
        var lastSync = tokenData.LastInboxSyncAt ?? DateTime.UtcNow.AddDays(-1);
        lastSync = lastSync.AddMinutes(-2);

        var unixTime = ((DateTimeOffset)lastSync).ToUnixTimeSeconds();

        string nextPageToken = null;
        int processed = 0;
        int limit = 100;

        DateTime? latestEmailTime = null; // 🔥 IMPORTANT

        do
        {
            var url = $"https://gmail.googleapis.com/gmail/v1/users/me/messages?q=after:{unixTime} in:inbox&maxResults=50";
            if (!string.IsNullOrEmpty(nextPageToken))
                url += $"&pageToken={nextPageToken}";

            var res = await http.GetAsync(url);
            var json = await res.Content.ReadAsStringAsync();

            if (!res.IsSuccessStatusCode)
            {
                Console.WriteLine($"❌ List API failed: {json}");
                return;
            }

            dynamic listObj = Newtonsoft.Json.JsonConvert.DeserializeObject(json);

            if (listObj.messages == null)
            {
                Console.WriteLine("📭 No messages");
                break;
            }

            foreach (var m in listObj.messages)
            {
                string messageId = m.id;

                // 🔥 Duplicate check
                bool exists = await _context.EmailReplies
                    .AnyAsync(x => x.MessageId == messageId);

                if (exists)
                    continue;

                // 🔥 Get full message
                var msgRes = await http.GetAsync(
                    $"https://gmail.googleapis.com/gmail/v1/users/me/messages/{messageId}");

                var msgJson = await msgRes.Content.ReadAsStringAsync();

                if (!msgRes.IsSuccessStatusCode)
                    continue;

                dynamic msg = Newtonsoft.Json.JsonConvert.DeserializeObject(msgJson);

                var headers = msg.payload.headers;

                string subject = GetHeader(headers, "Subject");
                string from = GetHeader(headers, "From");
                string inReplyTo = GetHeader(headers, "In-Reply-To");

                string body = ExtractBody(msg.payload);

                // 🔥 Gmail internal date
                DateTime emailDate = DateTime.UtcNow;
                if (msg.internalDate != null)
                {
                    long internalDate = (long)msg.internalDate;
                    emailDate = DateTimeOffset.FromUnixTimeMilliseconds(internalDate).UtcDateTime;
                }

                // =========================
                // 🔥 MATCHING LOGIC
                // =========================

                EmailLog sentMail = null;

                // 🔥 1. TrackingId (PRIMARY)
                var trackingId = EmailTrackingHelper.ExtractinboxTrackingId(body);

                if (trackingId != null)
                {
                    sentMail = await _context.EmailLogs
                        .FirstOrDefaultAsync(x => x.TrackingId == trackingId);

                    if (sentMail != null)
                        Console.WriteLine("🔥 Matched via TrackingId");
                }

                // 🔥 2. Fallback → In-Reply-To
                if (sentMail == null && !string.IsNullOrEmpty(inReplyTo))
                {
                    sentMail = await _context.EmailLogs
                        .FirstOrDefaultAsync(x => x.MessageId == inReplyTo);

                    if (sentMail != null)
                        Console.WriteLine("✅ Matched via In-Reply-To");
                }

                // ❌ Skip unrelated mails
                if (sentMail == null)
                {
                    Console.WriteLine("❌ Not our email → Skipped");
                    continue;
                }
                var cleanbody = Regex.Replace(body, @"TRACKING_ID:[0-9a-fA-F\-]{36}", "");
                cleanbody = Regex.Replace(cleanbody, @"(?m)^>\s?", "");
                cleanbody = cleanbody.Trim();
                // 🔥 Save reply
                _context.EmailReplies.Add(new EmailReplies
                {
                    ClientId = sentMail.ClientId,
                    ContactId = sentMail.ContactId,
                    CampaignId = sentMail.CampaignId,
                    MessageId = messageId,
                    InReplyTo = inReplyTo,
                    FromEmail = from,
                    Subject = subject,
                    Body = cleanbody,
                    TrackingId = sentMail.TrackingId,
                    Date = emailDate
                });

                // 🔥 Track latest email time
                if (latestEmailTime == null || emailDate > latestEmailTime)
                {
                    latestEmailTime = emailDate;
                }

                processed++;

                Console.WriteLine($"💾 Saved reply: {subject}");

                if (processed >= limit)
                    break;
            }

            if (processed >= limit)
                break;

            nextPageToken = listObj.nextPageToken;

        } while (!string.IsNullOrEmpty(nextPageToken));

        // 🔥 Step 3: Update Last Sync Time (CORRECT WAY)
        if (latestEmailTime != null)
        {
            tokenData.LastInboxSyncAt = latestEmailTime.Value.AddSeconds(-5); // buffer
        }

        await _context.SaveChangesAsync();

        Console.WriteLine($"✅ Sync Done: {processed} replies saved");
    }
    private string GetHeader(dynamic headers, string name)
    {
        foreach (var h in headers)
        {
            if (h.name == name)
                return h.value;
        }
        return "";
    }

    private string ExtractBody(dynamic payload)
    {
        try
        {
            // ✅ Direct body
            if (payload.body != null && payload.body.data != null)
            {
                return DecodeBase64(payload.body.data.ToString());
            }

            // ✅ Parts (most common case)
            if (payload.parts != null)
            {
                foreach (var part in payload.parts)
                {
                    if (part.mimeType == "text/plain" || part.mimeType == "text/html")
                    {
                        if (part.body != null && part.body.data != null)
                        {
                            return DecodeBase64(part.body.data.ToString());
                        }
                    }

                    // 🔁 Nested parts (IMPORTANT)
                    if (part.parts != null)
                    {
                        foreach (var subPart in part.parts)
                        {
                            if (subPart.body != null && subPart.body.data != null)
                            {
                                return DecodeBase64(subPart.body.data.ToString());
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Body parse error: {ex.Message}");
        }

        return "";
    }

    private string DecodeBase64(string input)
    {
        if (string.IsNullOrEmpty(input))
            return "";

        try
        {
            input = input.Replace('-', '+').Replace('_', '/');

            // padding fix
            switch (input.Length % 4)
            {
                case 2: input += "=="; break;
                case 3: input += "="; break;
            }

            var bytes = Convert.FromBase64String(input);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return "";
        }
    }
}