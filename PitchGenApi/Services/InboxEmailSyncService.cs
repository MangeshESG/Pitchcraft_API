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
    private readonly IInboxRepository _inboxRepository;

    public InboxEmailSyncService(AppDbContext context, EmailSendingHelper emailSending, IInboxRepository inboxRepository)
    {
        _context = context;
        _emailSending = emailSending;
        _inboxRepository = inboxRepository;
    }

    private string NormalizeMessageId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        value = value.Trim();

        // remove duplicate brackets/spaces
        value = value.Replace("<", "").Replace(">", "").Trim();

        return $"<{value}>";
    }

    public async Task SyncEmailsAsync(Inboxcredentials setting)
    {
        Console.WriteLine($"\n🚀 Starting sync for: {setting.Username}");

        using var client = new ImapClient();

        var option =
            _inboxRepository.GetSecureOption(setting.encryption);

        await client.ConnectAsync(
            setting.Host,
            setting.Port,
            option);

        Console.WriteLine("🔌 Connected to IMAP");

        await client.AuthenticateAsync(
            setting.Username,
            setting.Password);

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

            // =========================================
            // PROVIDER DETECTION
            // =========================================

            string provider = "Unknown";

            var dkim = msg.Headers["DKIM-Signature"];

            if (!string.IsNullOrEmpty(dkim))
            {
                if (dkim.Contains("google.com"))
                    provider = "Gmail";

                else if (
                    dkim.Contains("outlook.com") ||
                    dkim.Contains("microsoft.com"))
                {
                    provider = "Outlook";
                }
            }

            if (provider == "Unknown")
            {
                var receivedHeaders =
                    msg.Headers.Where(h => h.Field == "Received");

                foreach (var h in receivedHeaders)
                {
                    var val = h.Value.ToLower();

                    if (val.Contains("google.com"))
                    {
                        provider = "Gmail";
                        break;
                    }

                    if (
                        val.Contains("outlook.com") ||
                        val.Contains("microsoft.com"))
                    {
                        provider = "Outlook";
                        break;
                    }
                }
            }

            if (provider == "Unknown")
            {
                var mailer =
                    msg.Headers["X-Mailer"] ??
                    msg.Headers["User-Agent"];

                if (!string.IsNullOrEmpty(mailer))
                {
                    if (mailer.Contains("Outlook"))
                        provider = "Outlook";

                    else if (mailer.Contains("Apple Mail"))
                        provider = "Apple";

                    else if (mailer.Contains("Gmail"))
                        provider = "Gmail";
                }
            }

            Console.WriteLine($"📡 Provider: {provider}");

            // =========================================
            // BODY
            // =========================================

            string rawBody =
                msg.HtmlBody ??
                msg.TextBody ??
                "";

            // =========================================
            // TRACKING ID
            // =========================================

            var trackingId =
                EmailTrackingHelper.ExtractinboxTrackingId(rawBody);

            Console.WriteLine($"🎯 TrackingId: {trackingId}");

            // =========================================
            // CLEAN BODY
            // =========================================

            var body = ExtractOnlyReply(rawBody);

            // =========================================
            // NORMALIZED HEADERS
            // =========================================

            var normalizedMessageId =
                NormalizeMessageId(msg.MessageId);

            var normalizedInReplyTo =
                NormalizeMessageId(
                    msg.Headers["In-Reply-To"]);

            var rawReferences =
                msg.Headers["References"];
            var threadIndex =
                 msg.Headers["Thread-Index"];

            Console.WriteLine($"🧵 THREAD-INDEX: {threadIndex}");
            Console.WriteLine("=================================");
            Console.WriteLine($"📨 MESSAGE-ID: {normalizedMessageId}");
            Console.WriteLine($"↩️ IN-REPLY-TO: {normalizedInReplyTo}");
            Console.WriteLine($"📚 REFERENCES: {rawReferences}");
            Console.WriteLine("=================================");
            var fromEmail = msg.From.Mailboxes.FirstOrDefault()?.Address;

            // Skip own mails and our sent ThreadReplies
            if (!string.IsNullOrWhiteSpace(fromEmail) &&
                fromEmail.Equals(setting.EmailAddress, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Own mail skipped");
                continue;
            }

            // Skip if this is our own ThreadReply saved in EmailLogs
            bool isOurReply = await _context.EmailLogs
                .AnyAsync(x => x.MessageId == normalizedMessageId);

            if (isOurReply)
            {
                Console.WriteLine("Our ThreadReply skipped");
                continue;
            }
            // =========================================
            // FIND ORIGINAL SENT MAIL
            // =========================================

            EmailLog? sent = null;

            // -----------------------------------------
            // 1. TRACKING ID MATCH
            // -----------------------------------------

            if (trackingId != null)
            {
                sent = await _context.EmailLogs
                    .FirstOrDefaultAsync(x =>
                        x.TrackingId == trackingId);

                if (sent != null)
                {
                    Console.WriteLine(
                        "🔥 Matched via TrackingId");
                }
            }

            // -----------------------------------------
            // 2. IN-REPLY-TO MATCH
            // -----------------------------------------

            if (sent == null &&
                !string.IsNullOrWhiteSpace(normalizedInReplyTo))
            {
                sent = await _context.EmailLogs
                    .FirstOrDefaultAsync(x =>
                        x.MessageId == normalizedInReplyTo);

                if (sent != null)
                {
                    Console.WriteLine(
                        "✅ Matched via InReplyTo");
                }
            }

            // -----------------------------------------
            // 3. REFERENCES MATCH
            // -----------------------------------------

            if (sent == null &&
                !string.IsNullOrWhiteSpace(rawReferences))
            {
                var refs = rawReferences
                    .Split(' ',
                        StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => NormalizeMessageId(x))
                    .Distinct()
                    .ToList();

                foreach (var refId in refs)
                {
                    sent = await _context.EmailLogs
                        .FirstOrDefaultAsync(x =>
                            x.MessageId == refId);

                    if (sent != null)
                    {
                        Console.WriteLine(
                            "✅ Matched via References");

                        break;
                    }
                }
            }

            // =========================================
            // IF NO MATCH
            // =========================================

            if (sent == null)
            {
                Console.WriteLine(
                    "❌ No thread match found → Skipped");

                continue;
            }

            // =========================================
            // DUPLICATE CHECK
            // =========================================

            bool exists = await _context.EmailReplies
                .AnyAsync(x =>
                    x.MessageId == normalizedMessageId ||
                    x.MessageId == normalizedMessageId.Trim('<', '>'));

            if (exists)
            {
                Console.WriteLine(
                    "⚠️ Already exists → Skipped");

                continue;
            }

            // =========================================
            // SAVE REPLY
            // =========================================

            _context.EmailReplies.Add(new EmailReplies
            {
                ClientId = sent.ClientId,

                ContactId = sent.ContactId,

                CampaignId = sent.CampaignId,

                MessageId = normalizedMessageId,

                InReplyTo = normalizedInReplyTo,

                FromEmail = msg.From.ToString(),

                Subject = msg.Subject,

                Body = body,

                TrackingId =
                    trackingId ??
                    sent.TrackingId,

                Date = msg.Date.UtcDateTime,

                ThreadId =
                    !string.IsNullOrWhiteSpace(threadIndex)
                        ? threadIndex
                        : (sent.ThreadId ?? sent.MessageId)
            });

            Console.WriteLine("💾 Reply Saved");

            // =========================================
            // UPDATE UID
            // =========================================

            if (uid.Id > maxUid)
                maxUid = uid.Id;

            if (maxUid > setting.LastUid)
            {
                setting.LastUid = maxUid;

                _context.Inboxcredentials.Update(setting);

                Console.WriteLine(
                    $"📌 LastUid Updated → {maxUid}");
            }

            await _context.SaveChangesAsync();

            Console.WriteLine("✅ DB Saved");
        }

        await client.DisconnectAsync(true);

        Console.WriteLine(
            $"🔌 Disconnected: {setting.Username}");
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
                cleanbody = ExtractOnlyReply(cleanbody);
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
    public async Task SyncOutlookInboxAsync(EmailOAuthTokens tokenData)
    {
        Console.WriteLine($"🚀 Outlook Sync Start: {tokenData.Email}");

        // 🔥 Token refresh
        tokenData = await _emailSending.GetValidOutlookTokenAsync(tokenData.Id);

        if (tokenData == null)
        {
            Console.WriteLine("❌ Token invalid");
            return;
        }

        var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokenData.AccessToken);

        // 🔥 Last sync
        var lastSync = tokenData.LastInboxSyncAt ?? DateTime.UtcNow.AddDays(-1);
        lastSync = lastSync.AddMinutes(-2);

        string filterTime = lastSync.ToString("yyyy-MM-ddTHH:mm:ssZ");

        string url =
            $"https://graph.microsoft.com/v1.0/me/mailFolders/inbox/messages" +
            $"?$filter=receivedDateTime ge {filterTime}" +
            $"&$top=50" +
            $"&$select=subject,from,body,receivedDateTime,internetMessageHeaders";

        string nextLink = url;
        int processed = 0;
        int limit = 100;

        DateTime? latestEmailTime = null;

        while (!string.IsNullOrEmpty(nextLink))
        {
            var res = await http.GetAsync(nextLink);
            var json = await res.Content.ReadAsStringAsync();

            if (!res.IsSuccessStatusCode)
            {
                Console.WriteLine($"❌ Outlook API failed: {json}");
                return;
            }

            dynamic data = Newtonsoft.Json.JsonConvert.DeserializeObject(json);

            if (data.value == null)
            {
                Console.WriteLine("📭 No messages");
                break;
            }

            foreach (var msg in data.value)
            {
                string messageId = msg.id;

                // 🔥 Duplicate check
                bool exists = await _context.EmailReplies
                    .AnyAsync(x => x.MessageId == messageId);

                if (exists)
                    continue;

                string subject = msg.subject;
                string from = msg.from?.emailAddress?.address;

                // Skip our own sent mails
                if (!string.IsNullOrWhiteSpace(from) &&
                    from.Equals(tokenData.Email, StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine(" Own mail skipped");
                    continue;
                }

                // Skip if this is our own ThreadReply
                bool isOurReply = await _context.EmailLogs
                    .AnyAsync(x => x.MessageId == messageId ||
                                   x.MessageId == $"<{messageId.Trim('<', '>')}>");

                if (isOurReply)
                {
                    Console.WriteLine("Our ThreadReply skipped");
                    continue;
                }

                // =========================
                // 🔥 FIXED HEADER PARSE (NO LAMBDA)
                // =========================
                string inReplyTo = "";

                if (msg.internetMessageHeaders != null)
                {
                    foreach (var h in msg.internetMessageHeaders)
                    {
                        if (h.name == "In-Reply-To")
                        {
                            inReplyTo = h.value;
                            break;
                        }
                    }
                }

                string body = msg.body?.content ?? "";

                DateTime emailDate = msg.receivedDateTime != null
                    ? DateTime.Parse(msg.receivedDateTime.ToString()).ToUniversalTime()
                    : DateTime.UtcNow;

                // =========================
                // 🔥 MATCHING LOGIC
                // =========================
                EmailLog sentMail = null;

                var trackingId = EmailTrackingHelper.ExtractinboxTrackingId(body);

                if (trackingId != null)
                {
                    sentMail = await _context.EmailLogs
                        .FirstOrDefaultAsync(x => x.TrackingId == trackingId);

                    if (sentMail != null)
                        Console.WriteLine("🔥 Matched via TrackingId");
                }

                if (sentMail == null && !string.IsNullOrEmpty(inReplyTo))
                {
                    sentMail = await _context.EmailLogs
                        .FirstOrDefaultAsync(x => x.MessageId == inReplyTo);

                    if (sentMail != null)
                        Console.WriteLine("✅ Matched via InReplyTo");
                }

                if (sentMail == null)
                {
                    Console.WriteLine("❌ Not our email → Skipped");
                    continue;
                }

                // =========================
                // 🔥 CLEAN BODY (same as Gmail/IMAP)
                // =========================
                var cleanbody = Regex.Replace(body, @"TRACKING_ID:[0-9a-fA-F\-]{36}", "");
                cleanbody = Regex.Replace(cleanbody, @"(?m)^>\s?", "");
                cleanbody = cleanbody.Trim();
                cleanbody = ExtractOnlyReply(cleanbody);


                // 🔥 SAVE
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

                // 🔥 latest time
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

            nextLink = data["@odata.nextLink"];
        }

        // 🔥 Update sync time
        if (latestEmailTime != null)
        {
            tokenData.LastInboxSyncAt = latestEmailTime.Value.AddSeconds(-5);
        }

        await _context.SaveChangesAsync();

        Console.WriteLine($"✅ Outlook Sync Done: {processed} replies saved");
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
                    if (part.mimeType == "text/html")
                    {
                        if (part.body != null && part.body.data != null)
                        {
                            return DecodeBase64(part.body.data.ToString());
                        }
                    }

                    if (part.mimeType == "text/plain")
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
    //private string ExtractOnlyReply(string body)
    //{
    //    if (string.IsNullOrWhiteSpace(body))
    //        return "";

    //    // remove tracking id
    //    body = Regex.Replace(
    //        body,
    //        @"<div[^>]*>\s*TRACKING_ID:[0-9a-fA-F\-]{36}\s*</div>",
    //        "",
    //        RegexOptions.IgnoreCase);

    //    body = Regex.Replace(
    //        body,
    //        @"TRACKING_ID:[0-9a-fA-F\-]{36}",
    //        "",
    //        RegexOptions.IgnoreCase);

    //    // HTML case
    //    if (body.Contains("<"))
    //    {
    //        var quoteMatch = Regex.Match(
    //            body,
    //            @"<div[^>]*class=""[^""]*(gmail_quote|gmail_attr)[^""]*""[^>]*>|<blockquote[^>]*>|<hr[^>]*id=""replySplit[^""]*""[^>]*>",
    //            RegexOptions.IgnoreCase);

    //        if (quoteMatch.Success)
    //            body = body.Substring(0, quoteMatch.Index);

    //        body = Regex.Replace(
    //            body,
    //            @"<div[^>]*display\s*:\s*none[^>]*>.*?</div>",
    //            "",
    //            RegexOptions.IgnoreCase | RegexOptions.Singleline);

    //        body = Regex.Replace(
    //            body,
    //            @"<img[^>]*track/open[^>]*>",
    //            "",
    //            RegexOptions.IgnoreCase);

    //        return body.Trim();
    //    }

    //    // plain text case
    //    var textPatterns = new[]
    //    {
    //    @"On\s.+?wrote:",
    //    @"-----Original Message-----",
    //    @"^\s*From:\s.*$",
    //    @"^\s*Sent:\s.*$",
    //    @"^\s*To:\s.*$",
    //    @"^\s*Subject:\s.*$",
    //    @"^\s*_{5,}\s*$",
    //    @"^\s*>.*$"
    //};

    //    int cutIndex = -1;

    //    foreach (var pattern in textPatterns)
    //    {
    //        var match = Regex.Match(
    //            body,
    //            pattern,
    //            RegexOptions.IgnoreCase | RegexOptions.Multiline);

    //        if (match.Success)
    //        {
    //            if (cutIndex == -1 || match.Index < cutIndex)
    //                cutIndex = match.Index;
    //        }
    //    }

    //    if (cutIndex > 0)
    //        body = body.Substring(0, cutIndex);

    //    body = Regex.Replace(body, @"(\r?\n){3,}", "\n\n");

    //    return body.Trim();
    //}
    private string ExtractOnlyReply(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return "";

        // =========================
        // remove tracking id
        // =========================
        body = Regex.Replace(
            body,
            @"<div[^>]*>\s*TRACKING_ID:[0-9a-fA-F\-]{36}\s*</div>",
            "",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        body = Regex.Replace(
            body,
            @"TRACKING_ID:[0-9a-fA-F\-]{36}",
            "",
            RegexOptions.IgnoreCase);

        // remove tracking pixel
        body = Regex.Replace(
            body,
            @"<img[^>]*track/open[^>]*>",
            "",
            RegexOptions.IgnoreCase);

        // =========================
        // HTML case
        // =========================
        if (body.Contains("<"))
        {
            var quoteMatch = Regex.Match(
                body,
                @"<div[^>]*class=""[^""]*(gmail_quote|gmail_attr)[^""]*""[^>]*>" +
                @"|<blockquote[^>]*>" +
                @"|<div[^>]*id=""appendonsend""[^>]*>" +
                @"|<div[^>]*border-top:\s*solid[^>]*>" +
                @"|<div[^>]*id=""divRplyFwdMsg""[^>]*>" +
                @"|<b>\s*From:\s*</b>",
                RegexOptions.IgnoreCase);

            if (quoteMatch.Success)
                body = body.Substring(0, quoteMatch.Index);

            // hidden div remove
            body = Regex.Replace(
                body,
                @"<div[^>]*display\s*:\s*none[^>]*>.*?</div>",
                "",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            // tracking div remove
            body = Regex.Replace(
                body,
                @"<div[^>]*>.*?TRACKING_ID:[0-9a-fA-F\-]{36}.*?</div>",
                "",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            return body.Trim();
        }

        // =========================
        // plain text case
        // =========================
        var textPatterns = new[]
        {
        @"^On\s.+?wrote:",
        @"^-----Original Message-----",
        @"^\s*From:\s.*$",
        @"^\s*Sent:\s.*$",
        @"^\s*To:\s.*$",
        @"^\s*Subject:\s.*$",
        @"^\s*_{5,}\s*$",
        @"^\s*>.*$"
    };

        int cutIndex = -1;

        foreach (var pattern in textPatterns)
        {
            var match = Regex.Match(
                body,
                pattern,
                RegexOptions.IgnoreCase | RegexOptions.Multiline);

            if (match.Success)
            {
                if (cutIndex == -1 || match.Index < cutIndex)
                    cutIndex = match.Index;
            }
        }

        if (cutIndex > 0)
            body = body.Substring(0, cutIndex);

        // cleanup extra blank lines
        body = Regex.Replace(
            body,
            @"(\r?\n){3,}",
            Environment.NewLine + Environment.NewLine);

        return body.Trim();
    }
}