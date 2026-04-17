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
    private readonly ILogger<InboxEmailSyncService> _logger;

    public InboxEmailSyncService(AppDbContext context, ILogger<InboxEmailSyncService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task SyncEmailsAsync(Inboxcredentials setting)
    {
        _logger.LogInformation("Starting inbox sync for {Username}", setting.Username);

        try
        {
            using var client = new ImapClient();

            // Step: Connect
            await client.ConnectAsync(setting.Host, setting.Port, true);
            _logger.LogInformation("Connected to IMAP server {Host}:{Port} for {Username}", setting.Host, setting.Port, setting.Username);

            // Step: Authenticate
            await client.AuthenticateAsync(setting.Username, setting.Password);
            _logger.LogInformation("Authenticated successfully for {Username}", setting.Username);

            // Step: Open inbox
            var inbox = client.Inbox;
            await inbox.OpenAsync(FolderAccess.ReadOnly);
            _logger.LogInformation("Inbox opened for {Username}", setting.Username);

            // Step: Search all UIDs
            var uids = await inbox.SearchAsync(SearchQuery.All);
            _logger.LogInformation("Total UIDs in mailbox: {TotalUids}, LastUid: {LastUid} for {Username}", uids.Count, setting.LastUid, setting.Username);

            // Step: Filter new UIDs
            var newUids = uids
                .Where(uid => uid.Id > setting.LastUid)
                .OrderBy(uid => uid.Id)
                .ToList();

            _logger.LogInformation("New emails to process: {NewCount} for {Username}", newUids.Count, setting.Username);

            long maxUid = setting.LastUid;

            foreach (var uid in newUids)
            {
                try
                {
                    _logger.LogInformation("Processing UID {Uid} for {Username}", uid.Id, setting.Username);

                    // Step: Fetch message
                    var msg = await inbox.GetMessageAsync(uid);
                    _logger.LogInformation("Fetched message UID {Uid} | Subject: {Subject} | From: {From}", uid.Id, msg.Subject, msg.From);

                    var rawBody = msg.TextBody ?? msg.HtmlBody ?? "";

                    // Step: Extract TrackingId
                    var trackingId = EmailTrackingHelper.ExtractinboxTrackingId(rawBody);
                    _logger.LogInformation("UID {Uid} | TrackingId extracted: {TrackingId}", uid.Id);

                    // Step: Clean body
                    var body = Regex.Replace(rawBody, @"TRACKING_ID:[0-9a-fA-F\-]{36}", "");
                    body = Regex.Replace(body, @"(?m)^>\s?", "");
                    body = body.Trim();
                    _logger.LogInformation("UID {Uid} | Body cleaned, length: {BodyLength}", uid.Id, body.Length);

                    var rawInReplyTo = msg.Headers["In-Reply-To"];
                    var rawReferences = msg.Headers["References"];

                    EmailLog? sent = null;

                    // Step: Match via TrackingId
                    if (trackingId != null)
                    {
                        sent = await _context.EmailLogs
                            .FirstOrDefaultAsync(x => x.TrackingId == trackingId);

                        if (sent != null)
                            _logger.LogInformation("UID {Uid} | Matched via TrackingId: {TrackingId}", uid.Id, trackingId);
                        else
                            _logger.LogInformation("UID {Uid} | No match via TrackingId: {TrackingId}", uid.Id, trackingId);
                    }

                    // Step: Match via InReplyTo
                    if (sent == null && !string.IsNullOrEmpty(rawInReplyTo))
                    {
                        sent = await _context.EmailLogs
                            .FirstOrDefaultAsync(x => x.MessageId == rawInReplyTo);

                        if (sent != null)
                            _logger.LogInformation("UID {Uid} | Matched via InReplyTo: {InReplyTo}", uid.Id, rawInReplyTo);
                        else
                            _logger.LogInformation("UID {Uid} | No match via InReplyTo: {InReplyTo}", uid.Id, rawInReplyTo);
                    }

                    // Step: Match via References
                    if (sent == null && !string.IsNullOrEmpty(rawReferences))
                    {
                        var refs = rawReferences.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        _logger.LogInformation("UID {Uid} | Checking {RefCount} reference(s)", uid.Id, refs.Length);

                        foreach (var refId in refs)
                        {
                            sent = await _context.EmailLogs
                                .FirstOrDefaultAsync(x => x.MessageId == refId);

                            if (sent != null)
                            {
                                _logger.LogInformation("UID {Uid} | Matched via References: {RefId}", uid.Id, refId);
                                break;
                            }
                        }

                        if (sent == null)
                            _logger.LogInformation("UID {Uid} | No match via References", uid.Id);
                    }

                    // Step: Fallback match via sender email
                    if (sent == null)
                    {
                        var fromEmail = msg.From.Mailboxes.FirstOrDefault()?.Address?.ToLower();
                        _logger.LogInformation("UID {Uid} | Trying fallback match via sender email: {FromEmail}", uid.Id, fromEmail);

                        sent = await _context.EmailLogs
                            .Where(x => x.ToEmail.ToLower() == fromEmail)
                            .OrderByDescending(x => x.SentAt)
                            .FirstOrDefaultAsync();

                        if (sent != null)
                            _logger.LogInformation("UID {Uid} | Matched via fallback email: {FromEmail}", uid.Id, fromEmail);
                        else
                            _logger.LogWarning("UID {Uid} | No match found via any method for sender {FromEmail} — skipping", uid.Id, fromEmail);
                    }

                    if (sent == null)
                    {
                        _logger.LogWarning("UID {Uid} | Skipped: no matching EmailLog found for {Username}", uid.Id, setting.Username);
                        continue;
                    }

                    // Step: Duplicate check
                    bool exists = await _context.EmailReplies
                        .AnyAsync(x => x.MessageId == msg.MessageId);

                    if (exists)
                    {
                        _logger.LogInformation("UID {Uid} | Skipped: reply already exists in DB (MessageId: {MessageId})", uid.Id, msg.MessageId);
                        continue;
                    }

                    // Step: Save reply
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
                        TrackingId = trackingId,
                        Date = msg.Date.UtcDateTime
                    });

                    _logger.LogInformation("UID {Uid} | Reply queued for save | ClientId: {ClientId} | ContactId: {ContactId}", uid.Id, sent.ClientId, sent.ContactId);

                    if (uid.Id > maxUid)
                        maxUid = uid.Id;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed processing UID {Uid} for {Username}", uid.Id, setting.Username);
                }
            }

            // Step: Update LastUid
            if (maxUid > setting.LastUid)
            {
                setting.LastUid = maxUid;
                _context.Inboxcredentials.Update(setting);
                _logger.LogInformation("LastUid updated to {MaxUid} for {Username}", maxUid, setting.Username);
            }

            // Step: Save to DB
            await _context.SaveChangesAsync();
            _logger.LogInformation("All changes saved to DB for {Username}", setting.Username);

            // Step: Disconnect
            await client.DisconnectAsync(true);
            _logger.LogInformation("Disconnected from IMAP for {Username}", setting.Username);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error during inbox sync for {Username}", setting.Username);
            throw;
        }
    }
}
