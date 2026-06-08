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
using MimeKit;

public class InboxEmailSyncService : IInboxEmailSyncService
{
    private readonly AppDbContext _context;
    private readonly EmailSendingHelper _emailSending;
    private readonly IInboxRepository _inboxRepository;
    private readonly ContactRepository _contact;


    public InboxEmailSyncService(AppDbContext context, EmailSendingHelper emailSending, IInboxRepository inboxRepository, ContactRepository contact)
    {
        _context = context;
        _emailSending = emailSending;
        _inboxRepository = inboxRepository;
        _contact = contact;
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

        var option = _inboxRepository.GetSecureOption(setting.encryption);

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

        var sinceDate = DateTime.UtcNow.AddHours(-24);

        var uids = await inbox.SearchAsync(
            SearchQuery.DeliveredAfter(sinceDate)
        );

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
            try
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
                    else if (dkim.Contains("outlook.com") || dkim.Contains("microsoft.com"))
                        provider = "Outlook";
                }

                if (provider == "Unknown")
                {
                    var receivedHeaders = msg.Headers
                        .Where(h => h.Field == "Received");

                    foreach (var h in receivedHeaders)
                    {
                        var val = h.Value.ToLower();

                        if (val.Contains("google.com"))
                        {
                            provider = "Gmail";
                            break;
                        }

                        if (val.Contains("outlook.com") ||
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

                string rawBody = msg.HtmlBody ?? msg.TextBody ?? "";

                var trackingId =
                    EmailTrackingHelper.ExtractinboxTrackingId(rawBody);

                Console.WriteLine($"🎯 TrackingId: {trackingId}");

                Console.WriteLine($"RAW BODY LENGTH = {rawBody?.Length}");

                var body = ExtractOnlyReply(rawBody);

                Console.WriteLine($"EXTRACTED BODY LENGTH = {body?.Length}");
                //body ??= "";

                //if (body.Length > 4000)
                //    body = body.Substring(0, 4000);

                // =========================================
                // HEADERS
                // =========================================

                var normalizedMessageId =
                    NormalizeMessageId(msg.MessageId);

                normalizedMessageId =
                    string.IsNullOrWhiteSpace(normalizedMessageId)
                    ? Guid.NewGuid().ToString()
                    : normalizedMessageId;

                var normalizedInReplyTo =
                    NormalizeMessageId(msg.Headers["In-Reply-To"]);

                var rawReferences = msg.Headers["References"];

                var threadIndex = msg.Headers["Thread-Index"];

                if (!string.IsNullOrWhiteSpace(threadIndex))
                {
                    threadIndex = threadIndex.Substring(
                        0,
                        Math.Min(threadIndex.Length, 255));
                }

                Console.WriteLine($"🧵 THREAD-INDEX: {threadIndex}");
                Console.WriteLine("=================================");
                Console.WriteLine($"📨 MESSAGE-ID: {normalizedMessageId}");
                Console.WriteLine($"↩️ IN-REPLY-TO: {normalizedInReplyTo}");
                Console.WriteLine($"📚 REFERENCES: {rawReferences}");
                Console.WriteLine("=================================");

                var fromEmail =
                    msg.From.Mailboxes.FirstOrDefault()?.Address;

                var fromName =
                    msg.From.Mailboxes.FirstOrDefault()?.Name;

                // =========================================
                // SKIP OWN MAILS
                // =========================================

                if (!string.IsNullOrWhiteSpace(fromEmail) &&
                    fromEmail.Equals(
                        setting.EmailAddress,
                        StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("⚠️ Own mail skipped");
                    continue;
                }

                bool isOurSender = await _context.EmailLogs
                    .AnyAsync(x =>
                        x.SenderEmailId == fromEmail &&
                        x.ClientId == setting.ClientId &&
                        x.outboxid == setting.Outboxid);

                if (isOurSender)
                {
                    Console.WriteLine("⚠️ Our sender mail skipped");
                    continue;
                }

                bool isOurReply = await _context.EmailLogs
                    .AnyAsync(x =>
                        x.MessageId == normalizedMessageId);

                if (isOurReply)
                {
                    Console.WriteLine("⚠️ Our ThreadReply skipped");
                    continue;
                }

                // =========================================
                // FIND THREAD
                // =========================================

                EmailLog? sent = null;

                if (trackingId != null)
                {
                    sent = await _context.EmailLogs
                        .FirstOrDefaultAsync(x =>
                            x.TrackingId == trackingId);

                    if (sent != null)
                        Console.WriteLine("🔥 Matched via TrackingId");
                }

                if (sent == null &&
                    !string.IsNullOrWhiteSpace(normalizedInReplyTo))
                {
                    sent = await _context.EmailLogs
                        .FirstOrDefaultAsync(x =>
                            x.MessageId == normalizedInReplyTo);

                    if (sent != null)
                        Console.WriteLine("✅ Matched via InReplyTo");
                }

                if (sent == null &&
                    !string.IsNullOrWhiteSpace(rawReferences))
                {
                    var refs = rawReferences
                        .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .Select(NormalizeMessageId)
                        .Distinct()
                        .ToList();

                    foreach (var refId in refs)
                    {
                        sent = await _context.EmailLogs
                            .FirstOrDefaultAsync(x =>
                                x.MessageId == refId);

                        if (sent != null)
                        {
                            Console.WriteLine("✅ Matched via References");
                            break;
                        }
                    }
                }

                // =========================================
                // SAVE REPLY
                // =========================================

                if (sent != null)
                {
                    bool alreadyExists = await _context.EmailReplies
                        .AnyAsync(x =>
                            x.MessageId == normalizedMessageId);

                    if (alreadyExists)
                    {
                        Console.WriteLine("⚠️ Duplicate MessageId skipped");
                        continue;
                    }

                    var replyEntity = new EmailReplies
                    {
                        ClientId = sent.ClientId,

                        ContactId = sent.ContactId,

                        CampaignId = sent.CampaignId,

                        MessageId = normalizedMessageId,

                        InReplyTo = normalizedInReplyTo,

                        FromEmail = msg.From.ToString(),

                        Inboxid = setting.Id,

                        Subject = (msg.Subject ?? "")
                            .Substring(0,
                                Math.Min(
                                    (msg.Subject ?? "").Length,
                                    500)),

                        Body = body,

                        TrackingId = trackingId ?? sent.TrackingId,

                        Date = msg.Date.UtcDateTime,

                        ThreadId = !string.IsNullOrWhiteSpace(threadIndex)
                            ? threadIndex
                            : (sent.ThreadId ?? sent.MessageId)
                    };

                    _context.EmailReplies.Add(replyEntity);

                    await _context.SaveChangesAsync();

                    // =========================================
                    // SAVE ATTACHMENTS
                    // =========================================

                    if (msg.Attachments != null && msg.Attachments.Any())
                    {
                        var uploadPath = Path.Combine(
                            Directory.GetCurrentDirectory(),
                            "wwwroot",
                            "email-attachments");

                        if (!Directory.Exists(uploadPath))
                            Directory.CreateDirectory(uploadPath);

                        foreach (var attachment in msg.Attachments)
                        {
                            try
                            {
                                if (attachment is MimePart part)
                                {
                                    var originalFileName =
                                        string.IsNullOrWhiteSpace(part.FileName)
                                        ? "file"
                                        : part.FileName;

                                    var uniqueFileName =
                                        $"{Guid.NewGuid()}_{originalFileName}";

                                    var fullPath =
                                        Path.Combine(uploadPath, uniqueFileName);

                                    using (var stream = File.Create(fullPath))
                                    {
                                        await part.Content.DecodeToAsync(stream);
                                    }

                                    long? fileSize = null;

                                    if (File.Exists(fullPath))
                                    {
                                        fileSize =
                                            new FileInfo(fullPath).Length;
                                    }

                                    _context.EmailAttachments.Add(
                                        new EmailAttachment
                                        {
                                            MessageId = normalizedMessageId,

                                            FileName = uniqueFileName,

                                            OriginalFileName = originalFileName,

                                            ContentType =
                                                part.ContentType?.MimeType,

                                            FilePath =
                                                $"/email-attachments/{uniqueFileName}",

                                            FileSize = fileSize,

                                            Provider = provider,

                                            CreatedAt = DateTime.UtcNow
                                        });
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine("❌ Attachment Save Error");
                                Console.WriteLine(ex.Message);
                            }
                        }

                        await _context.SaveChangesAsync();

                        Console.WriteLine("📎 Reply Attachments Saved");
                    }

                    Console.WriteLine("💾 PK Reply Saved");
                }

                // =========================================
                // NORMAL MAIL
                // =========================================

                else
                {
                    var existingContact = await (
                        from c in _context.contacts
                        join d in _context.data_files
                            on c.DataFileId equals d.id
                        where c.email == fromEmail
                              && d.client_id == setting.ClientId
                        select c
                    ).FirstOrDefaultAsync();

                    // =========================================
                    // EXISTING CONTACT
                    // =========================================

                    if (existingContact != null)
                    {
                        bool alreadyReplyExists =
                            await _context.EmailReplies
                                .AnyAsync(x =>
                                    x.MessageId == normalizedMessageId);

                        if (alreadyReplyExists)
                        {
                            Console.WriteLine("⚠️ Duplicate MessageId skipped");
                            continue;
                        }

                        _context.EmailReplies.Add(new EmailReplies
                        {
                            ClientId = setting.ClientId,

                            ContactId = existingContact.id,

                            MessageId = normalizedMessageId,

                            InReplyTo = normalizedInReplyTo,

                            FromEmail = fromEmail,

                            Subject = (msg.Subject ?? "")
                                .Substring(0,
                                    Math.Min(
                                        (msg.Subject ?? "").Length,
                                        500)),

                            Inboxid = setting.Id,

                            Body = body,

                            TrackingId = Guid.NewGuid(),

                            Date = msg.Date.UtcDateTime,

                            ThreadId = !string.IsNullOrWhiteSpace(threadIndex)
                                ? threadIndex
                                : normalizedMessageId
                        });

                        Console.WriteLine("💾 Existing Contact Mail Saved In Replies");
                    }

                    // =========================================
                    // NEW CONTACT
                    // =========================================

                    else
                    {
                        trackingId = Guid.NewGuid();

                        var contactResult =
                            await _contact.SaveConversationContactAsync(
                                fromName,
                                fromEmail,
                                setting.ClientId);

                        if (!contactResult.Success)
                        {
                            Console.WriteLine(contactResult.Message);
                        }

                        bool alreadyExists =
                            await _context.InboxEmails
                                .AnyAsync(x =>
                                    x.MessageId == normalizedMessageId);

                        if (alreadyExists)
                        {
                            Console.WriteLine("⚠️ Duplicate MessageId skipped");
                            continue;
                        }

                        var inboxEntity = new InboxEmails
                        {
                            InboxId = setting.Id,

                            ClientId = setting.ClientId,

                            MessageId = normalizedMessageId,

                            InReplyTo = normalizedInReplyTo,

                            FromEmail = fromEmail,

                            FromName = fromName,

                            Subject = (msg.Subject ?? "")
                                .Substring(0,
                                    Math.Min(
                                        (msg.Subject ?? "").Length,
                                        500)),

                            Contactid = contactResult.ContactId,

                            Body = body,

                            Date = msg.Date.UtcDateTime,

                            IsRead = false,

                            Provider = provider,

                            TrackingId = trackingId,

                            ThreadId = !string.IsNullOrWhiteSpace(threadIndex)
                                ? threadIndex
                                : normalizedMessageId
                        };

                        _context.InboxEmails.Add(inboxEntity);

                        await _context.SaveChangesAsync();

                        // =========================================
                        // SAVE INBOX ATTACHMENTS
                        // =========================================

                        if (msg.Attachments != null && msg.Attachments.Any())
                        {
                            var uploadPath = Path.Combine(
                                Directory.GetCurrentDirectory(),
                                "wwwroot",
                                "email-attachments");

                            if (!Directory.Exists(uploadPath))
                                Directory.CreateDirectory(uploadPath);

                            foreach (var attachment in msg.Attachments)
                            {
                                try
                                {
                                    if (attachment is MimePart part)
                                    {
                                        var originalFileName =
                                            string.IsNullOrWhiteSpace(part.FileName)
                                            ? "file"
                                            : part.FileName;

                                        var uniqueFileName =
                                            $"{Guid.NewGuid()}_{originalFileName}";

                                        var fullPath =
                                            Path.Combine(uploadPath, uniqueFileName);

                                        using (var stream = File.Create(fullPath))
                                        {
                                            await part.Content.DecodeToAsync(stream);
                                        }

                                        long? fileSize = null;

                                        if (File.Exists(fullPath))
                                        {
                                            fileSize =
                                                new FileInfo(fullPath).Length;
                                        }

                                        _context.EmailAttachments.Add(
                                            new EmailAttachment
                                            {
                                                MessageId = normalizedMessageId,

                                                FileName = uniqueFileName,

                                                OriginalFileName = originalFileName,

                                                ContentType =
                                                    part.ContentType?.MimeType,

                                                FilePath =
                                                    $"/email-attachments/{uniqueFileName}",

                                                FileSize = fileSize,

                                                Provider = provider,

                                                CreatedAt = DateTime.UtcNow
                                            });
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine("❌ Inbox Attachment Save Error");
                                    Console.WriteLine(ex.Message);
                                }
                            }

                            await _context.SaveChangesAsync();

                            Console.WriteLine("📎 Inbox Attachments Saved");
                        }

                        Console.WriteLine("📥 Normal Inbox Mail Saved");
                    }
                }

                // =========================================
                // UPDATE UID
                // =========================================

                if (uid.Id > maxUid)
                    maxUid = uid.Id;

                if (maxUid > setting.LastUid)
                {
                    setting.LastUid = maxUid;

                    _context.Inboxcredentials.Update(setting);

                    Console.WriteLine($"📌 LastUid Updated → {maxUid}");
                }

                await _context.SaveChangesAsync();

                Console.WriteLine("✅ DB Saved");
            }
            catch (Exception ex)
            {
                Console.WriteLine("❌ ERROR");
                Console.WriteLine(ex.Message);

                if (ex.InnerException != null)
                {
                    Console.WriteLine("💥 INNER ERROR");
                    Console.WriteLine(ex.InnerException.Message);
                }
            }
        }

        await client.DisconnectAsync(true);

        Console.WriteLine($"🔌 Disconnected: {setting.Username}");
    }
    public async Task SyncGmailInboxAsync(EmailOAuthTokens tokenData)
    {
        Console.WriteLine($"🚀 Gmail Sync Start: {tokenData.Email}");

        tokenData = await _emailSending.GetValidGmailTokenAsync(tokenData.Id);

        if (tokenData == null)
        {
            Console.WriteLine("❌ Token invalid");
            return;
        }

        var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokenData.AccessToken);

        var lastSync = tokenData.LastInboxSyncAt ?? DateTime.UtcNow.AddDays(-1);
        lastSync = lastSync.AddMinutes(-2);

        var unixTime = ((DateTimeOffset)lastSync).ToUnixTimeSeconds();

        string nextPageToken = null;
        int processed = 0;
        int limit = 100;

        DateTime? latestEmailTime = null;

        do
        {
            var url =
                $"https://gmail.googleapis.com/gmail/v1/users/me/messages?q=after:{unixTime} in:inbox&maxResults=50";

            if (!string.IsNullOrEmpty(nextPageToken))
                url += $"&pageToken={nextPageToken}";

            var res = await http.GetAsync(url);
            var json = await res.Content.ReadAsStringAsync();

            if (!res.IsSuccessStatusCode)
            {
                Console.WriteLine($"❌ Gmail List API failed: {json}");
                return;
            }

            dynamic listObj =
                Newtonsoft.Json.JsonConvert.DeserializeObject(json);

            if (listObj.messages == null)
            {
                Console.WriteLine("📭 No messages");
                break;
            }

            foreach (var m in listObj.messages)
            {
                try
                {
                    string gmailMessageId = m.id;

                    var msgRes = await http.GetAsync(
                        $"https://gmail.googleapis.com/gmail/v1/users/me/messages/{gmailMessageId}?format=full");

                    var msgJson = await msgRes.Content.ReadAsStringAsync();

                    if (!msgRes.IsSuccessStatusCode)
                    {
                        Console.WriteLine($"❌ Gmail Message API failed: {msgJson}");
                        continue;
                    }

                    dynamic msg =
                        Newtonsoft.Json.JsonConvert.DeserializeObject(msgJson);

                    var headers = msg.payload.headers;

                    string messageId =
                        NormalizeMessageId(GetHeader(headers, "Message-ID"));

                    if (string.IsNullOrWhiteSpace(messageId))
                        messageId = gmailMessageId;

                    bool replyExists = await _context.EmailReplies
                        .AnyAsync(x =>
                            x.MessageId == messageId ||
                            x.MessageId == gmailMessageId);

                    if (replyExists)
                        continue;

                    bool inboxExistsAlready = await _context.InboxEmails
                        .AnyAsync(x =>
                            x.MessageId == messageId ||
                            x.MessageId == gmailMessageId);

                    if (inboxExistsAlready)
                        continue;

                    string gmailThreadId = msg.threadId?.ToString() ?? "";

                    string subject = GetHeader(headers, "Subject");
                    string fromHeader = GetHeader(headers, "From");

                    string fromName = "";
                    string fromAddress = fromHeader;

                    if (!string.IsNullOrWhiteSpace(fromHeader) &&
                        fromHeader.Contains("<"))
                    {
                        fromName =
                            fromHeader.Split('<')[0].Trim().Trim('"');

                        fromAddress = fromHeader.Substring(
                            fromHeader.IndexOf('<') + 1
                        ).TrimEnd('>');
                    }

                    string inReplyTo =
                        NormalizeMessageId(GetHeader(headers, "In-Reply-To"));

                    string references = GetHeader(headers, "References");

                    var referenceIds = (references ?? "")
                        .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .Select(NormalizeMessageId)
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct()
                        .ToList();

                    if (!string.IsNullOrWhiteSpace(fromAddress) &&
                        fromAddress.Equals(
                            tokenData.Email,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine("Own mail skipped");
                        continue;
                    }

                    bool isOurReply = await _context.EmailLogs
                        .AnyAsync(x =>
                            x.MessageId == messageId ||
                            x.MessageId == gmailMessageId);

                    if (isOurReply)
                    {
                        Console.WriteLine("⚠️ Our ThreadReply skipped");
                        continue;
                    }

                    bool isOurSenderGmail = await _context.EmailLogs
                        .AnyAsync(x =>
                            x.SenderEmailId == fromAddress &&
                            x.ClientId == tokenData.ClientId &&
                            x.outboxid == tokenData.Id);

                    if (isOurSenderGmail)
                    {
                        Console.WriteLine("Our sender mail skipped");
                        continue;
                    }

                    string body = ExtractBody(msg.payload);

                    var cleanbody = Regex.Replace(
                        body,
                        @"TRACKING_ID:[0-9a-fA-F\-]{36}",
                        "");

                    cleanbody = Regex.Replace(
                        cleanbody,
                        @"(?m)^>\s?",
                        "");

                    cleanbody = cleanbody.Trim();
                    cleanbody = ExtractOnlyReply(cleanbody);
                    cleanbody ??= "";

                    if (cleanbody.Length > 4000)
                        cleanbody = cleanbody.Substring(0, 4000);

                    DateTime emailDate = DateTime.UtcNow;

                    if (msg.internalDate != null)
                    {
                        long internalDate = (long)msg.internalDate;
                        emailDate =
                            DateTimeOffset
                            .FromUnixTimeMilliseconds(internalDate)
                            .UtcDateTime;
                    }

                    EmailLog sentMail = null;

                    var trackingId =
                        EmailTrackingHelper.ExtractinboxTrackingId(body);

                    if (trackingId != null)
                    {
                        sentMail = await _context.EmailLogs
                            .FirstOrDefaultAsync(x =>
                                x.TrackingId == trackingId);

                        if (sentMail != null)
                            Console.WriteLine("🔥 Matched via TrackingId");
                    }

                    if (sentMail == null &&
                        !string.IsNullOrWhiteSpace(inReplyTo))
                    {
                        sentMail = await _context.EmailLogs
                            .FirstOrDefaultAsync(x =>
                                x.MessageId == inReplyTo);

                        if (sentMail != null)
                            Console.WriteLine("✅ Matched via InReplyTo");
                    }

                    if (sentMail == null && referenceIds.Any())
                    {
                        foreach (var refId in referenceIds)
                        {
                            sentMail = await _context.EmailLogs
                                .FirstOrDefaultAsync(x =>
                                    x.MessageId == refId);

                            if (sentMail != null)
                            {
                                Console.WriteLine("✅ Matched via References");
                                break;
                            }
                        }
                    }

                    EmailReplies? savedReply = null;
                    InboxEmails? savedInbox = null;

                    if (sentMail != null)
                    {
                        savedReply = new EmailReplies
                        {
                            ClientId = sentMail.ClientId,
                            ContactId = sentMail.ContactId,
                            CampaignId = sentMail.CampaignId,

                            MessageId = messageId,
                            InReplyTo = inReplyTo,
                            FromEmail = fromAddress,
                            Subject = subject,
                            Inboxid = tokenData.Id,
                            Body = cleanbody,
                            TrackingId = trackingId ?? sentMail.TrackingId,
                            Date = emailDate,

                            ThreadId = !string.IsNullOrWhiteSpace(gmailThreadId)
                                ? gmailThreadId
                                : (sentMail.ThreadId ?? sentMail.MessageId)
                        };

                        _context.EmailReplies.Add(savedReply);

                        Console.WriteLine($"💾 Gmail Reply Saved: {subject}");
                    }
                    else
                    {
                        if (!tokenData.FullInboxSync)
                        {
                            Console.WriteLine("❌ Not our email → Skipped");
                            continue;
                        }

                        var existingContact = await _context.contacts
                            .Join(
                                _context.data_files,
                                c => c.DataFileId,
                                d => d.id,
                                (c, d) => new { c, d }
                            )
                            .Where(x =>
                                x.c.email == fromAddress &&
                                x.d.client_id == tokenData.ClientId)
                            .Select(x => x.c)
                            .FirstOrDefaultAsync();

                        var inboxExists = await _context.InboxEmails
                            .FirstOrDefaultAsync(x =>
                                x.FromEmail == fromAddress);

                        bool isReplyToInboxThread =
                            inboxExists != null &&
                            (
                                inboxExists.MessageId == inReplyTo ||
                                referenceIds.Contains(inboxExists.MessageId)
                            );

                        if (isReplyToInboxThread)
                        {
                            savedReply = new EmailReplies
                            {
                                ClientId = tokenData.ClientId,
                                ContactId = inboxExists.Contactid,

                                MessageId = messageId,
                                InReplyTo = inReplyTo,
                                FromEmail = fromAddress,
                                Subject = subject,
                                Inboxid = tokenData.Id,
                                Body = cleanbody,
                                TrackingId = inboxExists.TrackingId,
                                Date = emailDate,

                                ThreadId = !string.IsNullOrWhiteSpace(gmailThreadId)
                                    ? gmailThreadId
                                    : (inboxExists.ThreadId ??
                                       inboxExists.MessageId)
                            };

                            _context.EmailReplies.Add(savedReply);

                            Console.WriteLine("💾 Gmail Inbox Thread Reply Saved");
                        }
                        else if (existingContact != null)
                        {
                            savedReply = new EmailReplies
                            {
                                ClientId = tokenData.ClientId,
                                ContactId = existingContact.id,

                                MessageId = messageId,
                                InReplyTo = inReplyTo,
                                FromEmail = fromAddress,
                                Subject = subject,
                                Inboxid = tokenData.Id,
                                Body = cleanbody,
                                TrackingId = Guid.NewGuid(),
                                Date = emailDate,

                                ThreadId = !string.IsNullOrWhiteSpace(gmailThreadId)
                                    ? gmailThreadId
                                    : messageId
                            };

                            _context.EmailReplies.Add(savedReply);

                            Console.WriteLine(
                                "💾 Gmail Existing Contact Mail Saved In Replies");
                        }
                        else
                        {
                            trackingId = Guid.NewGuid();

                            var contactResult =
                                await _contact.SaveConversationContactAsync(
                                    fromName,
                                    fromAddress,
                                    tokenData.ClientId);

                            if (!contactResult.Success)
                            {
                                Console.WriteLine(contactResult.Message);
                            }

                            savedInbox = new InboxEmails
                            {
                                InboxId = tokenData.Id,
                                ClientId = tokenData.ClientId,

                                MessageId = messageId,
                                InReplyTo = inReplyTo,
                                FromEmail = fromAddress,
                                FromName = fromName,
                                Subject = subject,
                                Contactid = contactResult.ContactId,
                                Body = cleanbody,
                                Date = emailDate,
                                IsRead = false,
                                Provider = "Gmail",
                                TrackingId = trackingId,

                                ThreadId = !string.IsNullOrWhiteSpace(gmailThreadId)
                                    ? gmailThreadId
                                    : messageId
                            };

                            _context.InboxEmails.Add(savedInbox);

                            Console.WriteLine("📥 Gmail Normal Inbox Mail Saved");
                        }
                    }

                    await _context.SaveChangesAsync();

                    await SaveGmailAttachmentsAsync(
                        http,
                        gmailMessageId,
                        messageId,
                        msg.payload);

                    if (latestEmailTime == null ||
                        emailDate > latestEmailTime)
                    {
                        latestEmailTime = emailDate;
                    }

                    processed++;

                    if (processed >= limit)
                        break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("❌ Gmail ERROR");
                    Console.WriteLine(ex.Message);

                    if (ex.InnerException != null)
                    {
                        Console.WriteLine("💥 INNER ERROR");
                        Console.WriteLine(ex.InnerException.Message);
                    }
                }
            }

            if (processed >= limit)
                break;

            nextPageToken = listObj.nextPageToken;

        } while (!string.IsNullOrEmpty(nextPageToken));

        if (latestEmailTime != null)
        {
            tokenData.LastInboxSyncAt =
                latestEmailTime.Value.AddSeconds(-5);
        }

        await _context.SaveChangesAsync();

        Console.WriteLine($"✅ Gmail Sync Done: {processed} mails processed");
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

        // =========================================
        // LAST SYNC
        // =========================================

        var lastSync = tokenData.LastInboxSyncAt ?? DateTime.UtcNow.AddDays(-1);

        lastSync = lastSync.AddMinutes(-2);

        string filterTime = lastSync.ToString("yyyy-MM-ddTHH:mm:ssZ");

        string url =
            $"https://graph.microsoft.com/v1.0/me/mailFolders/inbox/messages" +
            $"?$filter=receivedDateTime ge {filterTime}" +
            $"&$top=50" +
            $"&$select=id,internetMessageId,subject,from,body,receivedDateTime,hasAttachments,internetMessageHeaders";

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

            dynamic data =
                Newtonsoft.Json.JsonConvert.DeserializeObject(json);

            if (data.value == null)
            {
                Console.WriteLine("📭 No messages");
                break;
            }

            foreach (var msg in data.value)
            {
                try
                {
                    string messageId = msg.internetMessageId;

                    if (string.IsNullOrWhiteSpace(messageId))
                        continue;

                    // =========================================
                    // DUPLICATE CHECK
                    // =========================================

                    bool replyExists = await _context.EmailReplies
                        .AnyAsync(x => x.MessageId == messageId);

                    if (replyExists)
                        continue;

                    bool inboxExistsAlready = await _context.InboxEmails
                        .AnyAsync(x => x.MessageId == messageId);

                    if (inboxExistsAlready)
                        continue;

                    string subject = msg.subject ?? "";

                    if (subject.Length > 500)
                        subject = subject.Substring(0, 500);

                    string from =
                        msg.from?.emailAddress?.address ?? "";

                    string fromName =
                        msg.from?.emailAddress?.name ?? "";

                    // =========================================
                    // SKIP OWN MAILS
                    // =========================================

                    if (!string.IsNullOrWhiteSpace(from) &&
                        from.Equals(
                            tokenData.Email,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine("⚠️ Own mail skipped");
                        continue;
                    }

                    bool isOurSenderOutlook = await _context.EmailLogs
                        .AnyAsync(x =>
                            x.SenderEmailId == from &&
                            x.ClientId == tokenData.ClientId &&
                            x.outboxid == tokenData.Id);

                    if (isOurSenderOutlook)
                    {
                        Console.WriteLine("⚠️ Our sender mail skipped");
                        continue;
                    }

                    bool isOurReply = await _context.EmailLogs
                        .AnyAsync(x =>
                            x.MessageId == messageId ||
                            x.MessageId ==
                            $"<{messageId.Trim('<', '>')}>");

                    if (isOurReply)
                    {
                        Console.WriteLine("⚠️ Our ThreadReply skipped");
                        continue;
                    }

                    // =========================================
                    // HEADERS
                    // =========================================

                    string inReplyTo = "";

                    string threadIndex = "";

                    if (msg.internetMessageHeaders != null)
                    {
                        foreach (var h in msg.internetMessageHeaders)
                        {
                            if (h.name == "In-Reply-To")
                            {
                                inReplyTo = h.value;
                            }

                            if (h.name == "Thread-Index")
                            {
                                threadIndex = h.value;
                            }
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(threadIndex))
                    {
                        threadIndex = threadIndex.Substring(
                            0,
                            Math.Min(threadIndex.Length, 255));
                    }

                    // =========================================
                    // BODY
                    // =========================================

                    string body = msg.body?.content ?? "";

                    DateTime emailDate =
                        msg.receivedDateTime != null
                        ? DateTimeOffset.Parse(
                            msg.receivedDateTime.ToString())
                            .UtcDateTime
                        : DateTime.UtcNow;

                    // =========================================
                    // CLEAN BODY
                    // =========================================

                    var cleanbody = Regex.Replace(
                        body,
                        @"TRACKING_ID:[0-9a-fA-F\-]{36}",
                        "");

                    cleanbody = Regex.Replace(
                        cleanbody,
                        @"(?m)^>\s?",
                        "");

                    cleanbody = cleanbody.Trim();

                    cleanbody = ExtractOnlyReply(cleanbody);

                    cleanbody ??= "";

                    if (cleanbody.Length > 4000)
                        cleanbody = cleanbody.Substring(0, 4000);

                    // =========================================
                    // MATCHING LOGIC
                    // =========================================

                    EmailLog sentMail = null;

                    var trackingId =
                        EmailTrackingHelper
                        .ExtractinboxTrackingId(body);

                    if (trackingId != null)
                    {
                        sentMail = await _context.EmailLogs
                            .FirstOrDefaultAsync(x =>
                                x.TrackingId == trackingId);

                        if (sentMail != null)
                            Console.WriteLine("🔥 Matched via TrackingId");
                    }

                    if (sentMail == null &&
                        !string.IsNullOrWhiteSpace(inReplyTo))
                    {
                        sentMail = await _context.EmailLogs
                            .FirstOrDefaultAsync(x =>
                                x.MessageId == inReplyTo);

                        if (sentMail != null)
                            Console.WriteLine("✅ Matched via InReplyTo");
                    }

                    // =========================================
                    // SAVE REPLY
                    // =========================================

                    EmailReplies? savedReply = null;

                    InboxEmails? savedInbox = null;

                    if (sentMail != null)
                    {
                        savedReply = new EmailReplies
                        {
                            ClientId = sentMail.ClientId,
                            ContactId = sentMail.ContactId,
                            CampaignId = sentMail.CampaignId,

                            MessageId = messageId,

                            InReplyTo = inReplyTo,

                            FromEmail = from,

                            Subject = subject,

                            Inboxid = tokenData.Id,

                            Body = cleanbody,

                            TrackingId = sentMail.TrackingId,

                            Date = emailDate,

                            ThreadId = !string.IsNullOrWhiteSpace(threadIndex)
                                ? threadIndex
                                : (sentMail.ThreadId ?? sentMail.MessageId)
                        };

                        _context.EmailReplies.Add(savedReply);

                        Console.WriteLine($"💾 PK Reply Saved: {subject}");
                    }

                    // =========================================
                    // NORMAL MAIL
                    // =========================================

                    else
                    {
                        if (!tokenData.FullInboxSync)
                        {
                            Console.WriteLine("❌ Not our email → Skipped");
                            continue;
                        }

                        // =========================================
                        // EXISTING CONTACT CHECK
                        // =========================================

                        var existingContact = await _context.contacts
                            .Join(
                                _context.data_files,
                                c => c.DataFileId,
                                d => d.id,
                                (c, d) => new { c, d }
                            )
                            .Where(x =>
                                x.c.email == from &&
                                x.d.client_id == tokenData.ClientId)
                            .Select(x => x.c)
                            .FirstOrDefaultAsync();

                        // =========================================
                        // EXISTING INBOX THREAD
                        // =========================================

                        var inboxExists = await _context.InboxEmails
                            .FirstOrDefaultAsync(x =>
                                x.FromEmail == from);

                        // =========================================
                        // REPLY TO EXISTING INBOX THREAD
                        // =========================================

                        if (inboxExists != null &&
                            inboxExists.MessageId == inReplyTo)
                        {
                            savedReply = new EmailReplies
                            {
                                ClientId = tokenData.ClientId,

                                ContactId = inboxExists.Contactid,

                                MessageId = messageId,

                                InReplyTo = inReplyTo,

                                FromEmail = from,

                                Subject = subject,

                                Inboxid = tokenData.Id,

                                Body = cleanbody,

                                TrackingId = inboxExists.TrackingId,

                                Date = emailDate,

                                ThreadId = !string.IsNullOrWhiteSpace(threadIndex)
                                    ? threadIndex
                                    : (inboxExists.ThreadId ??
                                       inboxExists.MessageId)
                            };

                            _context.EmailReplies.Add(savedReply);

                            Console.WriteLine("💾 Inbox Thread Reply Saved");
                        }

                        // =========================================
                        // EXISTING CONTACT
                        // =========================================

                        else if (existingContact != null)
                        {
                            savedReply = new EmailReplies
                            {
                                ClientId = tokenData.ClientId,

                                ContactId = existingContact.id,

                                MessageId = messageId,

                                InReplyTo = inReplyTo,

                                FromEmail = from,

                                Subject = subject,

                                Inboxid = tokenData.Id,

                                Body = cleanbody,

                                TrackingId = Guid.NewGuid(),

                                Date = emailDate,

                                ThreadId = !string.IsNullOrWhiteSpace(threadIndex)
                                    ? threadIndex
                                    : messageId
                            };

                            _context.EmailReplies.Add(savedReply);

                            Console.WriteLine("💾 Existing Contact Mail Saved In Replies");
                        }

                        // =========================================
                        // NEW CONTACT
                        // =========================================

                        else
                        {
                            trackingId = Guid.NewGuid();

                            var contactResult =
                                await _contact
                                .SaveConversationContactAsync(
                                    fromName,
                                    from,
                                    tokenData.ClientId);

                            if (!contactResult.Success)
                            {
                                Console.WriteLine(contactResult.Message);
                            }

                            savedInbox = new InboxEmails
                            {
                                InboxId = tokenData.Id,

                                ClientId = tokenData.ClientId,

                                MessageId = messageId,

                                InReplyTo = inReplyTo,

                                FromEmail = from,

                                FromName = fromName,

                                Subject = subject,

                                Contactid = contactResult.ContactId,

                                Body = cleanbody,

                                Date = emailDate,

                                IsRead = false,

                                TrackingId = trackingId,

                                ThreadId = !string.IsNullOrWhiteSpace(threadIndex)
                                    ? threadIndex
                                    : messageId
                            };

                            _context.InboxEmails.Add(savedInbox);

                            Console.WriteLine("📥 Normal Inbox Mail Saved");
                        }
                    }

                    // =========================================
                    // SAVE MAIN EMAIL FIRST
                    // =========================================

                    await _context.SaveChangesAsync();

                    // =========================================
                    // ATTACHMENTS
                    // =========================================

                    bool hasAttachments =
                        msg.hasAttachments != null &&
                        msg.hasAttachments == true;

                    if (hasAttachments)
                    {
                        Console.WriteLine("📎 Fetching attachments...");

                        string graphMessageId = msg.id;

                        var attachmentRes = await http.GetAsync(
                            $"https://graph.microsoft.com/v1.0/me/messages/{graphMessageId}/attachments");

                        var attachmentJson =
                            await attachmentRes.Content.ReadAsStringAsync();

                        if (attachmentRes.IsSuccessStatusCode)
                        {
                            dynamic attachmentData =
                                Newtonsoft.Json.JsonConvert
                                .DeserializeObject(attachmentJson);

                            if (attachmentData.value != null)
                            {
                                var uploadPath = Path.Combine(
                                    Directory.GetCurrentDirectory(),
                                    "wwwroot",
                                    "email-attachments");

                                if (!Directory.Exists(uploadPath))
                                    Directory.CreateDirectory(uploadPath);

                                foreach (var attachment in attachmentData.value)
                                {
                                    try
                                    {
                                        string attachmentType =
                                            attachment["@odata.type"]?.ToString();

                                        if (attachmentType != "#microsoft.graph.fileAttachment")
                                            continue;

                                        string originalFileName =
                                            attachment.name?.ToString() ?? "file";

                                        string contentType =
                                            attachment.contentType?.ToString()
                                            ?? "application/octet-stream";

                                        string base64 =
                                            attachment.contentBytes?.ToString();

                                        if (string.IsNullOrWhiteSpace(base64))
                                            continue;

                                        byte[] bytes =
                                            Convert.FromBase64String(base64);

                                        var uniqueFileName =
                                            $"{Guid.NewGuid()}_{originalFileName}";

                                        var fullPath =
                                            Path.Combine(uploadPath, uniqueFileName);

                                        await File.WriteAllBytesAsync(
                                            fullPath,
                                            bytes);

                                        long fileSize = bytes.Length;

                                        _context.EmailAttachments.Add(
                                            new EmailAttachment
                                            {
                                                MessageId = messageId,

                                                FileName = uniqueFileName,

                                                OriginalFileName = originalFileName,

                                                ContentType = contentType,

                                                FilePath =
                                                    $"/email-attachments/{uniqueFileName}",

                                                FileSize = fileSize,

                                                Provider = "Outlook",

                                                CreatedAt = DateTime.UtcNow
                                            });

                                        Console.WriteLine(
                                            $"📎 Attachment Saved: {originalFileName}");
                                    }
                                    catch (Exception ex)
                                    {
                                        Console.WriteLine(
                                            $"❌ Attachment Error: {ex.Message}");
                                    }
                                }

                                await _context.SaveChangesAsync();

                                Console.WriteLine("📎 Outlook Attachments Saved");
                            }
                        }
                        else
                        {
                            Console.WriteLine(
                                $"❌ Attachment Fetch Failed: {attachmentJson}");
                        }
                    }

                    // =========================================
                    // UPDATE SYNC TIME
                    // =========================================

                    if (latestEmailTime == null ||
                        emailDate > latestEmailTime)
                    {
                        latestEmailTime = emailDate;
                    }

                    processed++;

                    if (processed >= limit)
                        break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("❌ ERROR");
                    Console.WriteLine(ex.Message);

                    if (ex.InnerException != null)
                    {
                        Console.WriteLine("💥 INNER ERROR");
                        Console.WriteLine(ex.InnerException.Message);
                    }
                }
            }

            if (processed >= limit)
                break;

            nextLink = data["@odata.nextLink"];
        }

        // =========================================
        // UPDATE LAST SYNC
        // =========================================

        if (latestEmailTime != null)
        {
            tokenData.LastInboxSyncAt =
                latestEmailTime.Value.AddSeconds(-5);
        }

        await _context.SaveChangesAsync();

        Console.WriteLine($"✅ Outlook Sync Done: {processed} mails processed");
    }

    private async Task SaveGmailAttachmentsAsync(
        HttpClient http,
        string gmailMessageId,
        string messageId,
        dynamic payload)
    {
        if (payload == null)
            return;

        bool savedAny = false;

        var uploadPath = Path.Combine(
            Directory.GetCurrentDirectory(),
            "wwwroot",
            "email-attachments");

        if (!Directory.Exists(uploadPath))
            Directory.CreateDirectory(uploadPath);

        async Task ProcessPart(dynamic part)
        {
            if (part == null)
                return;

            if (part.parts != null)
            {
                foreach (var child in part.parts)
                {
                    await ProcessPart(child);
                }
            }

            string originalFileName = part.filename?.ToString();

            if (string.IsNullOrWhiteSpace(originalFileName))
                return;

            string contentType =
                part.mimeType?.ToString() ?? "application/octet-stream";

            string attachmentId = part.body?.attachmentId?.ToString();
            string base64 = part.body?.data?.ToString();

            if (string.IsNullOrWhiteSpace(base64) &&
                !string.IsNullOrWhiteSpace(attachmentId))
            {
                var attachmentRes = await http.GetAsync(
                    $"https://gmail.googleapis.com/gmail/v1/users/me/messages/{gmailMessageId}/attachments/{attachmentId}");

                var attachmentJson =
                    await attachmentRes.Content.ReadAsStringAsync();

                if (!attachmentRes.IsSuccessStatusCode)
                {
                    Console.WriteLine(
                        $"❌ Gmail Attachment Fetch Failed: {attachmentJson}");
                    return;
                }

                dynamic attachmentData =
                    Newtonsoft.Json.JsonConvert
                    .DeserializeObject(attachmentJson);

                base64 = attachmentData.data?.ToString();
            }

            if (string.IsNullOrWhiteSpace(base64))
                return;

            try
            {
                byte[] bytes = DecodeBase64Bytes(base64);

                var uniqueFileName =
                    $"{Guid.NewGuid()}_{originalFileName}";

                var fullPath =
                    Path.Combine(uploadPath, uniqueFileName);

                await File.WriteAllBytesAsync(fullPath, bytes);

                _context.EmailAttachments.Add(
                    new EmailAttachment
                    {
                        MessageId = messageId,
                        FileName = uniqueFileName,
                        OriginalFileName = originalFileName,
                        ContentType = contentType,
                        FilePath = $"/email-attachments/{uniqueFileName}",
                        FileSize = bytes.Length,
                        Provider = "Gmail",
                        CreatedAt = DateTime.UtcNow
                    });

                savedAny = true;

                Console.WriteLine(
                    $"📎 Gmail Attachment Saved: {originalFileName}");
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"❌ Gmail Attachment Error: {ex.Message}");
            }
        }

        await ProcessPart(payload);

        if (savedAny)
        {
            await _context.SaveChangesAsync();

            Console.WriteLine("📎 Gmail Attachments Saved");
        }
    }

    private string GetHeader(dynamic headers, string name)
    {
        foreach (var h in headers)
        {
            if (string.Equals(
                    h.name?.ToString(),
                    name,
                    StringComparison.OrdinalIgnoreCase))
            {
                return h.value;
            }
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
            var bytes = DecodeBase64Bytes(input);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return "";
        }
    }

    private byte[] DecodeBase64Bytes(string input)
    {
        input = input.Replace('-', '+').Replace('_', '/');

        switch (input.Length % 4)
        {
            case 2: input += "=="; break;
            case 3: input += "="; break;
        }

        return Convert.FromBase64String(input);
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
