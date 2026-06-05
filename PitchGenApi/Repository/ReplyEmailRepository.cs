using Microsoft.EntityFrameworkCore;
using MimeKit.Utils;
using PitchGenApi.Model.DTOs;
using System.Net.Mail;
using System.Net;
using PitchGenApi.Database;
using PitchGenApi.Interfaces;
using MimeKit;
using Newtonsoft.Json;
using System.Net.Http.Headers;
using System.Text;
using PitchGenApi.Model;

namespace PitchGenApi.Repository
{

    public class ReplyEmailRepository : IReplyEmailRepository
    {
        private readonly AppDbContext _context;
        private readonly EmailSendingHelper _emailSending;
        private readonly IInboxRepository _inboxRepository;
        public ReplyEmailRepository(AppDbContext context, EmailSendingHelper emailSending, IInboxRepository inboxRepository)
        {
            _context = context;
            _emailSending = emailSending;
            _inboxRepository = inboxRepository;
        }

        private static bool TryAddRecipients(
            InternetAddressList target,
            string recipients,
            out string error)
        {
            error = "";

            if (string.IsNullOrWhiteSpace(recipients))
                return true;

            try
            {
                var parsed = InternetAddressList.Parse(recipients.Trim());

                foreach (var mailbox in parsed.Mailboxes)
                {
                    target.Add(mailbox);
                }

                return true;
            }
            catch
            {
                foreach (var recipient in recipients.Split(
                    ',',
                    StringSplitOptions.RemoveEmptyEntries))
                {
                    var value = recipient.Trim();

                    if (string.IsNullOrWhiteSpace(value))
                        continue;

                    try
                    {
                        target.Add(MailboxAddress.Parse(value));
                    }
                    catch (Exception ex)
                    {
                        error = $"Invalid email address '{value}': {ex.Message}";
                        return false;
                    }
                }

                return true;
            }
        }

        public async Task<EmailSendResult> ReplyEmailUsingSmtp(Guid trackingid, int clientId, string replyBody, int outboxId, string BCC = "", string CC = "", List<IFormFile>? attachments = null)
        {
            try
            {
                var inbox = await _context.Inboxcredentials
                    .FirstOrDefaultAsync(x => x.Id == outboxId && x.ClientId == clientId);

                if (inbox == null)
                    return new EmailSendResult
                    {
                        Success = false,
                        Message = "Inbox not found"
                    };

                var smtpCredential = await _context.SmtpCredentials
                    .FirstOrDefaultAsync(x => x.Id == inbox.Outboxid);

                if (smtpCredential == null)
                    return new EmailSendResult
                    {
                        Success = false,
                        Message = "SMTP credential not found"
                    };

                var user = await _context.ClientDetails
                    .FirstOrDefaultAsync(x => x.Id == clientId);

                // =========================
                // FIRST EMAIL
                // =========================

                var firstLog = await _context.EmailLogs
                    .Where(x => x.TrackingId == trackingid)
                    .OrderBy(x => x.SentAt)
                    .FirstOrDefaultAsync();

                var firstInbox = await _context.InboxEmails
                    .Where(x => x.TrackingId == trackingid)
                    .OrderBy(x => x.CreatedAt)
                    .FirstOrDefaultAsync();

                var firstReply = await _context.EmailReplies
                    .Where(x => x.TrackingId == trackingid)
                    .OrderBy(x => x.Date)
                    .FirstOrDefaultAsync();

                DateTime? logDate = firstLog?.SentAt;
                DateTime? inboxDate = firstInbox?.CreatedAt;
                DateTime? replyDate = firstReply?.Date;

                var oldestDate = new[]
                {
                    logDate,
                    inboxDate,
                    replyDate
                }
                .Where(x => x != null)
                .Min();

                object firstSent = null;

                if (oldestDate == logDate)
                    firstSent = firstLog;
                else if (oldestDate == inboxDate)
                    firstSent = firstInbox;
                else
                    firstSent = firstReply;

                if (firstSent == null)
                {
                    return new EmailSendResult
                    {
                        Success = false,
                        Message = "Original email not found"
                    };
                }

                // =========================
                // LAST SENT / REPLY
                // =========================

                var lastSent = await _context.EmailLogs
                    .Where(x => x.TrackingId == trackingid)
                    .OrderByDescending(x => x.SentAt)
                    .FirstOrDefaultAsync();

                var lastReply = await _context.EmailReplies
                    .Where(x => x.TrackingId == trackingid)
                    .OrderByDescending(x => x.Date)
                    .FirstOrDefaultAsync();

                var latestMessageId =
                    lastReply?.MessageId ??
                    lastSent?.MessageId ??
                    firstLog?.MessageId ??
                    firstInbox?.MessageId;

                var replyToEmail =
                    lastReply?.FromEmail ??
                    lastSent?.ToEmail ??
                    firstInbox?.FromEmail ??
                    firstReply?.FromEmail;

                var threadSubject =
                    (
                        firstInbox?.Subject ??
                        firstReply?.Subject ??
                        lastSent?.Subject ??
                        lastReply?.Subject ??
                        "Re:"
                    ).Trim();

                if (!threadSubject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase))
                    threadSubject = "Re: " + threadSubject;

                // =========================
                // REFERENCES
                // =========================

                var references = await _context.EmailLogs
                    .Where(x => x.TrackingId == trackingid && !string.IsNullOrEmpty(x.MessageId))
                    .Select(x => x.MessageId)
                    .ToListAsync();

                references.AddRange(await _context.EmailReplies
                    .Where(x => x.TrackingId == trackingid && !string.IsNullOrEmpty(x.MessageId))
                    .Select(x => x.MessageId)
                    .ToListAsync());

                references = references.Distinct().ToList();

                var normalizedRefs = references
                    .Select(x =>
                    {
                        x = x.Trim();

                        if (!x.StartsWith("<"))
                            x = "<" + x;

                        if (!x.EndsWith(">"))
                            x += ">";

                        return x;
                    })
                    .ToList();

                // =========================
                // BODY
                // =========================

                string finalBody = replyBody;

                if (user?.IsTracking == true)
                {
                    finalBody = EmailTrackingHelper.InjectClickTracking(
                        finalBody,
                        trackingid.ToString());

                    finalBody += EmailTrackingHelper.GetPixelTag(
                        trackingid.ToString());
                }

                finalBody = EmailTrackingHelper.InjectinboxTracking(
                    finalBody,
                    trackingid.ToString());

                // =========================
                // MAIL
                // =========================

                var mail = new MimeMessage();

                mail.From.Add(new MailboxAddress(
                    smtpCredential.SenderName,
                    smtpCredential.FromEmail));

                mail.To.Add(MailboxAddress.Parse(replyToEmail));

                // CC
                if (!string.IsNullOrWhiteSpace(CC))
                {
                    foreach (var email in CC.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        mail.Cc.Add(MailboxAddress.Parse(email.Trim()));
                    }
                }

                // BCC
                if (!string.IsNullOrWhiteSpace(BCC))
                {
                    foreach (var email in BCC.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        mail.Bcc.Add(MailboxAddress.Parse(email.Trim()));
                    }
                }

                mail.Subject = threadSubject;

                var newMessageId =
                    $"<{MimeKit.Utils.MimeUtils.GenerateMessageId().Trim('<', '>')}>";

                mail.Headers.Replace(HeaderId.MessageId, newMessageId);

                var threadIndex = lastSent?.ThreadId ?? firstInbox?.ThreadId;

                if (!string.IsNullOrEmpty(threadIndex))
                {
                    try
                    {
                        byte[] parentBytes = Convert.FromBase64String(
                            threadIndex.PadRight(
                                threadIndex.Length +
                                (4 - threadIndex.Length % 4) % 4,
                                '='));

                        byte[] childBytes = new byte[parentBytes.Length + 5];

                        Array.Copy(parentBytes, childBytes, parentBytes.Length);

                        new Random().NextBytes(
                            new Span<byte>(childBytes, parentBytes.Length, 5));

                        mail.Headers.Add(
                            "Thread-Index",
                            Convert.ToBase64String(childBytes));
                    }
                    catch
                    {
                        mail.Headers.Add("Thread-Index", threadIndex);
                    }

                    mail.Headers.Add(
                        "Thread-Topic",
                        threadSubject.Replace("Re: ", "").Trim());
                }

                // =========================
                // THREAD FIX
                // =========================

                if (!string.IsNullOrEmpty(latestMessageId))
                {
                    var normalizedLatest = latestMessageId.Trim();

                    if (!normalizedLatest.StartsWith("<"))
                        normalizedLatest = "<" + normalizedLatest;

                    if (!normalizedLatest.EndsWith(">"))
                        normalizedLatest += ">";

                    mail.InReplyTo = normalizedLatest;

                    normalizedRefs.RemoveAll(x => x == normalizedLatest);

                    normalizedRefs.Add(normalizedLatest);
                }

                mail.Headers.Replace(
                    HeaderId.References,
                    string.Join(
                        " ",
                        normalizedRefs.Where(x => !string.IsNullOrEmpty(x))));

                // =========================
                // BODY BUILDER + ATTACHMENTS
                // =========================

                var bodyBuilder = new BodyBuilder
                {
                    HtmlBody = finalBody
                };

                if (attachments != null && attachments.Any())
                {
                    foreach (var file in attachments)
                    {
                        if (file.Length > 0)
                        {
                            using var ms = new MemoryStream();

                            await file.CopyToAsync(ms);

                            bodyBuilder.Attachments.Add(
                                file.FileName,
                                ms.ToArray(),
                                ContentType.Parse(file.ContentType));
                        }
                    }
                }

                mail.Body = bodyBuilder.ToMessageBody();

                // =========================
                // SEND
                // =========================

                using var smtpClient = new MailKit.Net.Smtp.SmtpClient();

                await smtpClient.ConnectAsync(
                    smtpCredential.Server,
                    smtpCredential.Port,
                    _inboxRepository.GetSecureOption(
                        smtpCredential.SecurityType));

                await smtpClient.AuthenticateAsync(
                    smtpCredential.Username,
                    smtpCredential.Password);

                await smtpClient.SendAsync(mail);

                await smtpClient.DisconnectAsync(true);

                // =========================
                // SAVE LOG
                // =========================

                var emailLog = new EmailLog
                {
                    ClientId = clientId,

                    ContactId = firstLog?.ContactId ??
                                firstInbox?.Contactid ??
                                firstReply?.ContactId,

                    ToEmail = replyToEmail,

                    Subject = mail.Subject,

                    Body = replyBody,

                    SenderEmailId = smtpCredential.FromEmail,

                    EmailSenderName = smtpCredential.SenderName,

                    Provider = "SMTP",

                    outboxid = smtpCredential.Id,

                    IsSuccess = true,

                    SentAt = DateTime.UtcNow,

                    TrackingId = trackingid,

                    MessageId = newMessageId,

                    ThreadId = threadIndex,

                    process_name = "ThreadReply"
                };

                _context.EmailLogs.Add(emailLog);

                await _context.SaveChangesAsync();

                // =========================
                // SAVE ATTACHMENTS
                // =========================

                if (attachments != null && attachments.Any())
                {
                    var uploadPath = Path.Combine(
                        Directory.GetCurrentDirectory(),
                        "wwwroot",
                        "email-attachments");

                    if (!Directory.Exists(uploadPath))
                        Directory.CreateDirectory(uploadPath);

                    foreach (var file in attachments)
                    {
                        if (file.Length <= 0)
                            continue;

                        var uniqueFileName =
                            $"{Guid.NewGuid()}_{file.FileName}";

                        var fullPath =
                            Path.Combine(uploadPath, uniqueFileName);

                        using (var stream = new FileStream(
                            fullPath,
                            FileMode.Create))
                        {
                            await file.CopyToAsync(stream);
                        }

                        _context.EmailAttachments.Add(new EmailAttachment
                        {
                            MessageId = newMessageId,

                            FileName = uniqueFileName,

                            OriginalFileName = file.FileName,

                            ContentType = file.ContentType,

                            FilePath = $"/email-attachments/{uniqueFileName}",

                            FileSize = file.Length,

                            Provider = "SMTP",

                            CreatedAt = DateTime.UtcNow
                        });
                    }

                    await _context.SaveChangesAsync();
                }

                return new EmailSendResult
                {
                    Success = true,
                    Message = "Reply sent with attachment successfully"
                };
            }
            catch (Exception ex)
            {
                return new EmailSendResult
                {
                    Success = false,
                    Message = ex.InnerException?.Message ?? ex.Message
                };
            }
        }
        public async Task<EmailSendResult> ReplyEmailUsingGmailApi(Guid trackingid, int clientId, string replyBody, int outboxId, string BCC = "", string CC = "", List<IFormFile>? attachments = null)
        {
            var user = await _context.ClientDetails
                .FirstOrDefaultAsync(x => x.Id == clientId);

            var tokenData = await _emailSending.GetValidGmailTokenAsync(outboxId);
            if (tokenData == null)
            {
                return new EmailSendResult
                {
                    Success = false,
                    Message = "Gmail not connected."
                };
            }
            var firstLog = await _context.EmailLogs
                 .Where(x => x.TrackingId == trackingid)
                 .OrderBy(x => x.SentAt)
                 .FirstOrDefaultAsync();

            var firstInbox = await _context.InboxEmails
                .Where(x => x.TrackingId == trackingid)
                .OrderBy(x => x.CreatedAt)
                .FirstOrDefaultAsync();

            var firstReply = await _context.EmailReplies
                .Where(x => x.TrackingId == trackingid)
                .OrderBy(x => x.Date)
                .FirstOrDefaultAsync();

            var lastSent = await _context.EmailLogs
                .Where(x => x.TrackingId == trackingid)
                .OrderByDescending(x => x.SentAt)
                .FirstOrDefaultAsync();

            var lastReply = await _context.EmailReplies
                .Where(x => x.TrackingId == trackingid)
                .OrderByDescending(x => x.Date)
                .FirstOrDefaultAsync();

            if (firstLog == null && firstInbox == null && firstReply == null)
            {
                return new EmailSendResult
                {
                    Success = false,
                    Message = "Original email not found"
                };
            }

            // 🔥 STEP 2: correct last messageId
            var latestMessageId =
                 lastReply?.MessageId ??
                 lastSent?.MessageId ??
                 firstReply?.MessageId ??
                 firstLog?.MessageId ??
                 firstInbox?.MessageId;

            // 🔥 STEP 3: threadId (VERY IMPORTANT)
            string threadId =
                lastReply?.ThreadId ??
                lastSent?.ThreadId ??
                firstReply?.ThreadId ??
                firstInbox?.ThreadId ??
                firstLog?.ThreadId;
            try
            {
                string finalBody = replyBody;

                // ✅ tracking
                if (user.IsTracking)
                {
                    finalBody = EmailTrackingHelper.InjectClickTracking(finalBody, trackingid.ToString());
                    finalBody += EmailTrackingHelper.GetPixelTag(trackingid.ToString());
                }

                finalBody = EmailTrackingHelper.InjectinboxTracking(finalBody, trackingid.ToString());

                // =========================
                // MIME BUILD
                // =========================
                var mimeMessage = new MimeMessage();

                mimeMessage.From.Add(new MailboxAddress(tokenData.SenderName, tokenData.Email));

                var replyToEmail =
                    lastReply?.FromEmail ??
                    lastSent?.ToEmail ??
                    firstInbox?.FromEmail ??
                    firstReply?.FromEmail ??
                    firstLog?.ToEmail;

                if (string.IsNullOrWhiteSpace(replyToEmail))
                {
                    return new EmailSendResult
                    {
                        Success = false,
                        Message = "Reply email address not found"
                    };
                }

                if (!TryAddRecipients(
                        mimeMessage.To,
                        replyToEmail,
                        out var toError) ||
                    !mimeMessage.To.Mailboxes.Any())
                {
                    return new EmailSendResult
                    {
                        Success = false,
                        Message = string.IsNullOrWhiteSpace(toError)
                            ? "Reply email address not found"
                            : toError
                    };
                }

                var threadSubject =
                    firstInbox?.Subject ??
                    firstReply?.Subject ??
                    lastSent?.Subject ??
                    lastReply?.Subject ??
                    "Re:";

                if (!threadSubject.StartsWith("Re:",
                    StringComparison.OrdinalIgnoreCase))
                {
                    threadSubject = "Re: " + threadSubject;
                }

                mimeMessage.Subject = threadSubject;
                if (!string.IsNullOrWhiteSpace(CC))
                {
                    if (!TryAddRecipients(
                            mimeMessage.Cc,
                            CC,
                            out var ccError))
                    {
                        return new EmailSendResult
                        {
                            Success = false,
                            Message = ccError
                        };
                    }
                }
                if (!string.IsNullOrWhiteSpace(BCC))
                {
                    if (!TryAddRecipients(
                            mimeMessage.Bcc,
                            BCC,
                            out var bccError))
                    {
                        return new EmailSendResult
                        {
                            Success = false,
                            Message = bccError
                        };
                    }
                }

                if (!string.IsNullOrWhiteSpace(latestMessageId))
                {
                    if (!latestMessageId.StartsWith("<"))
                        latestMessageId = "<" + latestMessageId;

                    if (!latestMessageId.EndsWith(">"))
                        latestMessageId += ">";
                }
                // 🔥 THREAD HEADERS (IMPORTANT)
                if (!string.IsNullOrWhiteSpace(latestMessageId))
                {
                    mimeMessage.Headers.Add(
                        "In-Reply-To",
                        latestMessageId);

                    mimeMessage.Headers.Add(
                        "References",
                        latestMessageId);
                }

                mimeMessage.Headers.Add("X-Tracking-Id", trackingid.ToString());

                var bodyBuilder = new BodyBuilder
                {
                    HtmlBody = finalBody
                };

                if (attachments != null && attachments.Any())
                {
                    foreach (var file in attachments)
                    {
                        using var attachmentStream = new MemoryStream();

                        await file.CopyToAsync(attachmentStream);

                        bodyBuilder.Attachments.Add(
                            file.FileName,
                            attachmentStream.ToArray(),
                            ContentType.Parse(
                                file.ContentType ??
                                "application/octet-stream"));
                    }
                }

                mimeMessage.Body = bodyBuilder.ToMessageBody();
                mimeMessage.Body.ContentType.Charset = "utf-8";

                // =========================
                // ENCODE
                // =========================
                using var emailStream = new MemoryStream();
                await mimeMessage.WriteToAsync(emailStream);

                var rawMessage = Convert.ToBase64String(emailStream.ToArray())
                    .Replace('+', '-')
                    .Replace('/', '_')
                    .Replace("=", "");

                // =========================
                // GMAIL API CALL
                // =========================
                var client = new HttpClient();
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", tokenData.AccessToken);

                object payload;

                if (!string.IsNullOrWhiteSpace(threadId))
                {
                    payload = new
                    {
                        raw = rawMessage,
                        threadId = threadId
                    };
                }
                else
                {
                    payload = new
                    {
                        raw = rawMessage
                    };
                }

                var response = await client.PostAsJsonAsync(
                    "https://gmail.googleapis.com/gmail/v1/users/me/messages/send",
                    payload);

                var resultJson = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                    throw new Exception(resultJson);

                // 🔥 parse response
                var gmailResponse = JsonConvert.DeserializeObject<dynamic>(resultJson);

                string gmailMessageId = gmailResponse.id;
                string newThreadId = gmailResponse.threadId;

                // =========================
                // SAVE LOG
                // =========================
                _context.EmailLogs.Add(new EmailLog
                {
                    ClientId = clientId,
                    ToEmail = replyToEmail,
                    Subject = mimeMessage.Subject,
                    Body = replyBody,
                    SenderEmailId = tokenData.Email,
                    EmailSenderName = tokenData.SenderName,
                    Provider = "Gmail",
                    outboxid = tokenData.Id,
                    IsSuccess = true,
                    SentAt = DateTime.UtcNow,
                    TrackingId = trackingid,
                    MessageId = gmailMessageId,
                    ThreadId = newThreadId, // 🔥 update thread
                    process_name = "ThreadReply",
                    ContactId = firstLog?.ContactId ?? firstInbox?.Contactid ?? firstReply?.ContactId,
                });

                await _context.SaveChangesAsync();
                // =========================================
                // SAVE ATTACHMENTS DB
                // =========================================

                if (attachments != null && attachments.Any())
                {
                    var uploadPath = Path.Combine(
                        Directory.GetCurrentDirectory(),
                        "wwwroot",
                        "email-attachments");

                    if (!Directory.Exists(uploadPath))
                    {
                        Directory.CreateDirectory(uploadPath);
                    }

                    foreach (var file in attachments)
                    {
                        if (file == null || file.Length <= 0)
                            continue;

                        var uniqueFileName =
                            $"{Guid.NewGuid()}_{file.FileName}";

                        var fullPath =
                            Path.Combine(uploadPath, uniqueFileName);

                        using (var stream = new FileStream(
                            fullPath,
                            FileMode.Create))
                        {
                            await file.CopyToAsync(stream);
                        }

                        _context.EmailAttachments.Add(
                            new EmailAttachment
                            {
                                MessageId = gmailMessageId,

                                FileName = uniqueFileName,

                                OriginalFileName = file.FileName,

                                ContentType = file.ContentType,

                                FilePath =
                                    $"/email-attachments/{uniqueFileName}",

                                FileSize = file.Length,

                                Provider = "Gmail",

                                CreatedAt = DateTime.UtcNow
                            });
                    }

                    await _context.SaveChangesAsync();
                }
                return new EmailSendResult
                {
                    Success = true,
                    Message = "Reply sent in SAME Gmail thread ✅"
                };
            }
            catch (Exception ex)
            {
                return new EmailSendResult
                {
                    Success = false,
                    Message = ex.Message
                };
            }
        }
        public async Task<EmailSendResult> ReplyEmailUsingOutlookApi(Guid trackingid, int clientId, string replyBody, int outboxId, string BCC = "", string CC = "", List<IFormFile>? attachments = null)
        {
            try
            {
                var user = await _context.ClientDetails
                    .FirstOrDefaultAsync(x => x.Id == clientId);

                var tokenData = await _emailSending
                    .GetValidOutlookTokenAsync(outboxId);

                if (tokenData == null)
                {
                    return new EmailSendResult
                    {
                        Success = false,
                        Message = "Outlook not connected"
                    };
                }

                // =========================================
                // FIRST EMAIL
                // =========================================

                var firstLog = await _context.EmailLogs
                    .Where(x => x.TrackingId == trackingid)
                    .OrderBy(x => x.SentAt)
                    .FirstOrDefaultAsync();

                var firstInbox = await _context.InboxEmails
                    .Where(x => x.TrackingId == trackingid)
                    .OrderBy(x => x.CreatedAt)
                    .FirstOrDefaultAsync();

                var firstReply = await _context.EmailReplies
                    .Where(x => x.TrackingId == trackingid)
                    .OrderBy(x => x.Date)
                    .FirstOrDefaultAsync();

                DateTime? logDate = firstLog?.SentAt;
                DateTime? inboxDate = firstInbox?.CreatedAt;
                DateTime? replyDate = firstReply?.Date;

                var oldestDate = new[]
                {
                    logDate,
                    inboxDate,
                    replyDate
                }
                .Where(x => x != null)
                .Min();

                object firstSent = null;

                if (oldestDate == logDate)
                {
                    firstSent = firstLog;
                }
                else if (oldestDate == inboxDate)
                {
                    firstSent = firstInbox;
                }
                else
                {
                    firstSent = firstReply;
                }

                if (firstSent == null)
                {
                    return new EmailSendResult
                    {
                        Success = false,
                        Message = "Original email not found"
                    };
                }

                // =========================================
                // LAST SENT / REPLY
                // =========================================

                var lastSent = await _context.EmailLogs
                    .Where(x => x.TrackingId == trackingid)
                    .OrderByDescending(x => x.SentAt)
                    .FirstOrDefaultAsync();

                var lastReply = await _context.EmailReplies
                    .Where(x => x.TrackingId == trackingid)
                    .OrderByDescending(x => x.Date)
                    .FirstOrDefaultAsync();

                // =========================================
                // MESSAGE LINKING
                // =========================================

                var latestMessageId =
                    lastReply?.MessageId ??
                    lastSent?.MessageId ??
                    firstReply?.MessageId ??
                    firstLog?.MessageId ??
                    firstInbox?.MessageId;

                var replyToEmail =
                    lastReply?.FromEmail ??
                    lastSent?.ToEmail ??
                    firstInbox?.FromEmail ??
                    firstReply?.FromEmail;

                var threadSubject =
                    firstInbox?.Subject ??
                    firstReply?.Subject ??
                    lastSent?.Subject ??
                    lastReply?.Subject ??
                    "Re:";

                threadSubject = threadSubject.Trim();

                if (!threadSubject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase))
                {
                    threadSubject = "Re: " + threadSubject;
                }

                if (threadSubject.Length > 500)
                {
                    threadSubject = threadSubject.Substring(0, 500);
                }

                if (string.IsNullOrWhiteSpace(latestMessageId))
                {
                    return new EmailSendResult
                    {
                        Success = false,
                        Message = "MessageId missing"
                    };
                }

                // =========================================
                // BODY
                // =========================================

                string finalBody = replyBody;

                if (user?.IsTracking == true)
                {
                    finalBody = EmailTrackingHelper.InjectClickTracking(
                        finalBody,
                        trackingid.ToString());

                    finalBody += EmailTrackingHelper.GetPixelTag(
                        trackingid.ToString());
                }

                finalBody = EmailTrackingHelper.InjectinboxTracking(
                    finalBody,
                    trackingid.ToString());

                using var client = new HttpClient();

                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue(
                        "Bearer",
                        tokenData.AccessToken);

                // =========================================
                // NORMALIZE MESSAGE ID
                // =========================================

                var normalizedMsgId = latestMessageId.Trim();

                if (!normalizedMsgId.StartsWith("<"))
                    normalizedMsgId = "<" + normalizedMsgId;

                if (!normalizedMsgId.EndsWith(">"))
                    normalizedMsgId += ">";

                // =========================================
                // FIND OUTLOOK MESSAGE
                // =========================================

                var encodedMsgId =
                    Uri.EscapeDataString(normalizedMsgId);

                var findResponse = await client.GetAsync(
                    $"https://graph.microsoft.com/v1.0/me/messages?$filter=internetMessageId eq '{normalizedMsgId}'&$top=1");

                var findJson =
                    await findResponse.Content.ReadAsStringAsync();

                if (!findResponse.IsSuccessStatusCode)
                {
                    return new EmailSendResult
                    {
                        Success = false,
                        Message = findJson
                    };
                }

                dynamic found =
                    Newtonsoft.Json.JsonConvert.DeserializeObject(findJson);

                string graphId = "";

                if (found?.value != null &&
                    found.value.Count > 0)
                {
                    graphId =
                        found.value[0]?.id?.ToString();
                }

                if (string.IsNullOrWhiteSpace(graphId))
                {
                    return new EmailSendResult
                    {
                        Success = false,
                        Message =
                            $"Outlook message not found for MessageId: {normalizedMsgId}"
                    };
                }

                // =========================================
                // CREATE DRAFT REPLY
                // =========================================

                var safeId = Uri.EscapeDataString(graphId);

                var createDraftResponse = await client.PostAsync(
                    $"https://graph.microsoft.com/v1.0/me/messages/{safeId}/createReply",
                    null);

                var createDraftJson =
                    await createDraftResponse.Content.ReadAsStringAsync();

                if (!createDraftResponse.IsSuccessStatusCode)
                {
                    return new EmailSendResult
                    {
                        Success = false,
                        Message = createDraftJson
                    };
                }

                dynamic draftObj =
                    Newtonsoft.Json.JsonConvert.DeserializeObject(createDraftJson);

                string draftId =
                    draftObj?.id?.ToString();

                if (string.IsNullOrWhiteSpace(draftId))
                {
                    return new EmailSendResult
                    {
                        Success = false,
                        Message = "Draft reply creation failed"
                    };
                }

                // =========================================
                // UPDATE BODY
                // =========================================

                var updatePayload = new
                {
                    body = new
                    {
                        contentType = "HTML",
                        content = finalBody
                    }
                };

                var patchRequest = new HttpRequestMessage(
                    new HttpMethod("PATCH"),
                    $"https://graph.microsoft.com/v1.0/me/messages/{draftId}")
                {
                    Content = JsonContent.Create(updatePayload)
                };

                var updateResponse =
                    await client.SendAsync(patchRequest);

                var updateResult =
                    await updateResponse.Content.ReadAsStringAsync();

                if (!updateResponse.IsSuccessStatusCode)
                {
                    return new EmailSendResult
                    {
                        Success = false,
                        Message = updateResult
                    };
                }

                // =========================================
                // CC + BCC
                // =========================================

                var ccRecipients = string.IsNullOrWhiteSpace(CC)
                    ? new object[] { }
                    : CC.Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(x => new
                        {
                            emailAddress = new
                            {
                                address = x.Trim()
                            }
                        }).ToArray();

                var bccRecipients = string.IsNullOrWhiteSpace(BCC)
                    ? new object[] { }
                    : BCC.Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(x => new
                        {
                            emailAddress = new
                            {
                                address = x.Trim()
                            }
                        }).ToArray();

                if (ccRecipients.Any() || bccRecipients.Any())
                {
                    var recipientPayload = new
                    {
                        ccRecipients,
                        bccRecipients
                    };

                    var recipientRequest = new HttpRequestMessage(
                        new HttpMethod("PATCH"),
                        $"https://graph.microsoft.com/v1.0/me/messages/{draftId}")
                    {
                        Content = JsonContent.Create(recipientPayload)
                    };

                    var recipientResponse =
                        await client.SendAsync(recipientRequest);

                    var recipientResult =
                        await recipientResponse.Content.ReadAsStringAsync();

                    if (!recipientResponse.IsSuccessStatusCode)
                    {
                        return new EmailSendResult
                        {
                            Success = false,
                            Message = recipientResult
                        };
                    }
                }

                // =========================================
                // ATTACHMENTS
                // =========================================

                if (attachments != null && attachments.Any())
                {
                    foreach (var file in attachments)
                    {
                        if (file == null || file.Length <= 0)
                            continue;

                        using var ms = new MemoryStream();

                        await file.CopyToAsync(ms);

                        var attachmentPayload =
                            new Dictionary<string, object>
                            {
                        { "@odata.type", "#microsoft.graph.fileAttachment" },
                        { "name", file.FileName },
                        { "contentBytes", Convert.ToBase64String(ms.ToArray()) },
                        { "contentType", file.ContentType ?? "application/octet-stream" }
                            };

                        var attachmentResponse =
                            await client.PostAsJsonAsync(
                                $"https://graph.microsoft.com/v1.0/me/messages/{draftId}/attachments",
                                attachmentPayload);

                        var attachmentResult =
                            await attachmentResponse.Content.ReadAsStringAsync();

                        if (!attachmentResponse.IsSuccessStatusCode)
                        {
                            return new EmailSendResult
                            {
                                Success = false,
                                Message = attachmentResult
                            };
                        }
                    }
                }

                // =========================================
                // SEND
                // =========================================

                var sendResponse = await client.PostAsync(
                    $"https://graph.microsoft.com/v1.0/me/messages/{draftId}/send",
                    null);

                var sendResult =
                    await sendResponse.Content.ReadAsStringAsync();

                if (!sendResponse.IsSuccessStatusCode)
                {
                    return new EmailSendResult
                    {
                        Success = false,
                        Message = sendResult
                    };
                }

                // =========================================
                // SAVE LOG
                // =========================================

                string newMessageId =
                    $"<{MimeKit.Utils.MimeUtils.GenerateMessageId().Trim('<', '>')}>";

                _context.EmailLogs.Add(new EmailLog
                {
                    ClientId = clientId,

                    ContactId =
                        firstLog?.ContactId ??
                        firstInbox?.Contactid ??
                        firstReply?.ContactId,

                    ToEmail = replyToEmail,

                    Subject = threadSubject,

                    Body = replyBody,

                    SenderEmailId = tokenData.Email,

                    EmailSenderName = tokenData.SenderName,

                    Provider = "Outlook",

                    outboxid = tokenData.Id,

                    IsSuccess = true,

                    SentAt = DateTime.UtcNow,

                    TrackingId = trackingid,

                    MessageId = newMessageId,

                    ThreadId =
                        lastSent?.ThreadId ??
                        firstInbox?.ThreadId ??
                        firstReply?.ThreadId,

                    process_name = "ThreadReply"
                });

                await _context.SaveChangesAsync();

                // =========================================
                // SAVE ATTACHMENTS DB
                // =========================================

                if (attachments != null && attachments.Any())
                {
                    var uploadPath = Path.Combine(
                        Directory.GetCurrentDirectory(),
                        "wwwroot",
                        "email-attachments");

                    if (!Directory.Exists(uploadPath))
                    {
                        Directory.CreateDirectory(uploadPath);
                    }

                    foreach (var file in attachments)
                    {
                        if (file == null || file.Length <= 0)
                            continue;

                        var uniqueFileName =
                            $"{Guid.NewGuid()}_{file.FileName}";

                        var fullPath =
                            Path.Combine(uploadPath, uniqueFileName);

                        using (var stream = new FileStream(
                            fullPath,
                            FileMode.Create))
                        {
                            await file.CopyToAsync(stream);
                        }

                        _context.EmailAttachments.Add(
                            new EmailAttachment
                            {
                                MessageId = newMessageId,

                                FileName = uniqueFileName,

                                OriginalFileName = file.FileName,

                                ContentType = file.ContentType,

                                FilePath =
                                    $"/email-attachments/{uniqueFileName}",

                                FileSize = file.Length,

                                Provider = "Outlook",

                                CreatedAt = DateTime.UtcNow
                            });
                    }

                    await _context.SaveChangesAsync();
                }

                return new EmailSendResult
                {
                    Success = true,
                    Message =
                        "Reply sent in SAME Outlook conversation with attachments ✅"
                };
            }
            catch (Exception ex)
            {
                return new EmailSendResult
                {
                    Success = false,
                    Message = ex.ToString()
                };
            }
        }
    }
}
