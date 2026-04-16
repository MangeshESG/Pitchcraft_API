using MailKit.Net.Imap;
using MailKit.Search;
using MailKit;
using PitchGenApi.Database;
using PitchGenApi.Interfaces;
using PitchGenApi.Model;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;

public class InboxEmailSyncService : IInboxEmailSyncService
{
    private readonly AppDbContext _context;

    public InboxEmailSyncService(AppDbContext context)
    {
        _context = context;
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

            var rawBody = msg.TextBody ?? msg.HtmlBody ?? "";

            // 🔥 STEP 1: Extract TrackingId
            var trackingId = EmailTrackingHelper.ExtractinboxTrackingId(rawBody);
            Console.WriteLine($"🎯 TrackingId: {trackingId}");

            // 🔥 STEP 2: CLEAN BODY (remove tracking id + quoted text)
            var body = Regex.Replace(rawBody, @"TRACKING_ID:[0-9a-fA-F\-]{36}", "");
            //body = Regex.Replace(body, @"(?m)^>.*$", "");
            body = Regex.Replace(body, @"(?m)^>\s?", "");// remove quoted lines
            body = body.Trim();

            var rawInReplyTo = msg.Headers["In-Reply-To"];
            var rawReferences = msg.Headers["References"];

            EmailLog? sent = null;

            // 🔥 STEP 0: TrackingId match
            if (trackingId != null)
            {
                sent = await _context.EmailLogs
                    .FirstOrDefaultAsync(x => x.TrackingId == trackingId);

                if (sent != null)
                    Console.WriteLine("🔥 Matched via TrackingId");
            }

            // STEP 1: InReplyTo
            if (sent == null && !string.IsNullOrEmpty(rawInReplyTo))
            {
                sent = await _context.EmailLogs
                    .FirstOrDefaultAsync(x => x.MessageId == rawInReplyTo);

                if (sent != null)
                    Console.WriteLine("✅ Matched via InReplyTo");
            }

            // STEP 2: References
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

            // STEP 3: fallback email
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

            // ✅ duplicate check
            bool exists = await _context.EmailReplies
                .AnyAsync(x => x.MessageId == msg.MessageId);

            if (exists)
            {
                Console.WriteLine("⚠️ Already exists → Skipped");
                continue;
            }

            // 🔥 SAVE (WITHOUT TRACKING ID)
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
                TrackingId = trackingId, // ❌ not saving actual tracking id
                Date = msg.Date.UtcDateTime
            });

            Console.WriteLine("💾 Saved");

            if (uid.Id > maxUid)
                maxUid = uid.Id;
        }

        // 🔥 Update LastUid
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
}