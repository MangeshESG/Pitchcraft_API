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

namespace PitchGenApi.Repository
{
    
    public class ReplyEmailRepository : IReplyEmailRepository
    {
        private readonly AppDbContext _context;
        private readonly EmailSendingHelper _emailSending;
        private readonly IInboxRepository _inboxRepository;
        public ReplyEmailRepository(AppDbContext context, EmailSendingHelper emailSending,IInboxRepository inboxRepository)
        {
            _context = context;
            _emailSending = emailSending;
            _inboxRepository = inboxRepository;      
        }
        public async Task<EmailSendResult> ReplyEmailUsingSmtp( Guid trackingid, int clientId, string replyBody, int outboxId, string BccEmail = "")
        {
            try
            {
                var inbox = await _context.Inboxcredentials
                    .FirstOrDefaultAsync(x => x.Id == outboxId && x.ClientId == clientId);

                if (inbox == null)
                    return new EmailSendResult { Success = false, Message = "Inbox not found" };

                var smtpCredential = await _context.SmtpCredentials
                    .FirstOrDefaultAsync(x => x.Id == inbox.Outboxid);

                if (smtpCredential == null)
                    return new EmailSendResult { Success = false, Message = "SMTP credential not found" };

                var user = await _context.ClientDetails
                    .FirstOrDefaultAsync(x => x.Id == clientId);

                // =========================
                // FIRST EMAIL (Logs / Inbox / Replies)
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

                // oldest mail
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
                    return new EmailSendResult
                    {
                        Success = false,
                        Message = "Original email not found"
                    };

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

                // =========================
                // MESSAGE LINKING (SAFE)
                // =========================
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
                        if (!x.StartsWith("<")) x = "<" + x;
                        if (!x.EndsWith(">")) x += ">";
                        return x;
                    })
                    .ToList();

                // =========================
                // BODY + TRACKING
                // =========================
                string finalBody = replyBody;

                if (user?.IsTracking == true)
                {
                    finalBody = EmailTrackingHelper.InjectClickTracking(finalBody, trackingid.ToString());
                    finalBody += EmailTrackingHelper.GetPixelTag(trackingid.ToString());
                }

                finalBody = EmailTrackingHelper.InjectinboxTracking(finalBody, trackingid.ToString());

                // =========================
                // MAIL SETUP
                // =========================
                var mail = new MimeMessage();

                mail.From.Add(new MailboxAddress(smtpCredential.SenderName, smtpCredential.FromEmail));
                mail.To.Add(MailboxAddress.Parse(replyToEmail));

                if (!string.IsNullOrWhiteSpace(BccEmail))
                    mail.Bcc.Add(MailboxAddress.Parse(BccEmail));

                mail.Subject = threadSubject;

                var newMessageId = $"<{MimeKit.Utils.MimeUtils.GenerateMessageId().Trim('<', '>')}>";

                mail.Headers.Replace(HeaderId.MessageId, newMessageId);

                var threadIndex = lastSent?.ThreadId ?? firstInbox?.ThreadId;

                if (!string.IsNullOrEmpty(threadIndex))
                {
                    try
                    {
                        byte[] parentBytes = Convert.FromBase64String(threadIndex.PadRight(
                            threadIndex.Length + (4 - threadIndex.Length % 4) % 4, '='));

                        byte[] childBytes = new byte[parentBytes.Length + 5];

                        Array.Copy(parentBytes, childBytes, parentBytes.Length);

                        new Random().NextBytes(new Span<byte>(childBytes, parentBytes.Length, 5));

                        mail.Headers.Add("Thread-Index", Convert.ToBase64String(childBytes));
                    }
                    catch
                    {
                        mail.Headers.Add("Thread-Index", threadIndex);
                    }

                    mail.Headers.Add("Thread-Topic", threadSubject.Replace("Re: ", "").Trim());
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

                mail.Headers.Replace(HeaderId.References,
                    string.Join(" ", normalizedRefs.Where(x => !string.IsNullOrEmpty(x))));

                mail.Body = new BodyBuilder { HtmlBody = finalBody }.ToMessageBody();

                // =========================
                // SEND MAIL
                // =========================
                using var smtpClient = new MailKit.Net.Smtp.SmtpClient();

                await smtpClient.ConnectAsync(
                    smtpCredential.Server,
                    smtpCredential.Port,
                    _inboxRepository.GetSecureOption(smtpCredential.SecurityType));

                await smtpClient.AuthenticateAsync(
                    smtpCredential.Username,
                    smtpCredential.Password);

                await smtpClient.SendAsync(mail);
                await smtpClient.DisconnectAsync(true);

                // =========================
                // SAVE LOG
                // =========================
                _context.EmailLogs.Add(new EmailLog
                {
                    ClientId = clientId,
                    ContactId = firstLog?.ContactId ?? firstInbox?.Contactid ?? firstReply?.ContactId,
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
                });

                await _context.SaveChangesAsync();

                return new EmailSendResult
                {
                    Success = true,
                    Message = "Reply sent in SAME thread successfully"
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

        public async Task<EmailSendResult> ReplyEmailUsingGmailApi(Guid trackingid, int clientId, string replyBody, int outboxId, string BccEmail = "")
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

            // 🔥 STEP 1: latest sent + reply find
            var lastSent = await _context.EmailLogs
                .Where(x => x.TrackingId == trackingid)
                .OrderByDescending(x => x.SentAt)
                .FirstOrDefaultAsync();

            var lastReply = await _context.EmailReplies
                .Where(x => x.TrackingId == trackingid)
                .OrderByDescending(x => x.Date)
                .FirstOrDefaultAsync();

            if (lastSent == null)
            {
                return new EmailSendResult
                {
                    Success = false,
                    Message = "Original email not found"
                };
            }

            // 🔥 STEP 2: correct last messageId
            string lastMessageId = null;

            if (lastReply != null && lastSent != null)
            {
                lastMessageId = lastReply.Date > lastSent.SentAt
                    ? lastReply.MessageId
                    : lastSent.MessageId;
            }
            else if (lastReply != null)
            {
                lastMessageId = lastReply.MessageId;
            }
            else
            {
                lastMessageId = lastSent.MessageId;
            }

            // 🔥 STEP 3: threadId (VERY IMPORTANT)
            string threadId = lastReply?.ThreadId ?? lastSent.ThreadId;

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
                mimeMessage.To.Add(new MailboxAddress("", lastSent.ToEmail));

                mimeMessage.Subject = lastSent.Subject.StartsWith("Re:")
                    ? lastSent.Subject
                    : "Re: " + lastSent.Subject;

                // 🔥 THREAD HEADERS (IMPORTANT)
                if (!string.IsNullOrEmpty(lastMessageId))
                {
                    mimeMessage.Headers.Add("In-Reply-To", lastMessageId);
                    mimeMessage.Headers.Add("References", lastMessageId);
                }

                mimeMessage.Headers.Add("X-Tracking-Id", trackingid.ToString());

                var bodyBuilder = new BodyBuilder
                {
                    HtmlBody = finalBody
                };

                mimeMessage.Body = bodyBuilder.ToMessageBody();
                mimeMessage.Body.ContentType.Charset = "utf-8";

                // =========================
                // ENCODE
                // =========================
                using var ms = new MemoryStream();
                await mimeMessage.WriteToAsync(ms);

                var rawMessage = Convert.ToBase64String(ms.ToArray())
                    .Replace('+', '-')
                    .Replace('/', '_')
                    .Replace("=", "");

                // =========================
                // GMAIL API CALL
                // =========================
                var client = new HttpClient();
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", tokenData.AccessToken);

                var payload = new
                {
                    raw = rawMessage,
                    threadId = threadId // 🔥🔥 THIS MAKES SAME THREAD
                };

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
                    ToEmail = lastSent.ToEmail,
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
                    process_name = "ThreadReply"
                });

                await _context.SaveChangesAsync();

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
        public async Task<EmailSendResult> ReplyEmailUsingOutlookApi(Guid trackingid, int clientId, string replyBody, int outboxId, string BccEmail = "")
        {
            var user = await _context.ClientDetails
                .FirstOrDefaultAsync(x => x.Id == clientId);

            var tokenData = await _emailSending.GetValidOutlookTokenAsync(outboxId);

            if (tokenData == null)
            {
                return new EmailSendResult
                {
                    Success = false,
                    Message = "Outlook not connected"
                };
            }

            // =========================
            // FIRST EMAIL (Logs / Inbox / Replies)
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

            // =========================
            // MESSAGE LINKING
            // =========================
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
                 (
                     firstInbox?.Subject ??
                     firstReply?.Subject ??
                     lastSent?.Subject ??
                     lastReply?.Subject ??
                     "Re:"
                 ).Trim();

            if (!threadSubject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase))
                threadSubject = "Re: " + threadSubject;

            if (string.IsNullOrWhiteSpace(latestMessageId))
            {
                return new EmailSendResult
                {
                    Success = false,
                    Message = "MessageId missing"
                };
            }

            try
            {
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

                // STEP 1: Find CURRENT Graph Id from internetMessageId
                var normalizedMsgId = latestMessageId.Trim();

                if (!normalizedMsgId.StartsWith("<"))
                    normalizedMsgId = "<" + normalizedMsgId;

                if (!normalizedMsgId.EndsWith(">"))
                    normalizedMsgId += ">";

                var encodedMsgId = normalizedMsgId.Replace("'", "''");

                var findResponse = await client.GetAsync(
                    $"https://graph.microsoft.com/v1.0/me/messages?$filter=internetMessageId eq '{encodedMsgId}'");

                var findJson = await findResponse.Content.ReadAsStringAsync();

                if (!findResponse.IsSuccessStatusCode)
                    throw new Exception(findJson);

                dynamic found =
                    Newtonsoft.Json.JsonConvert.DeserializeObject(findJson);

                string graphId = found?.value?[0]?.id?.ToString();

                if (string.IsNullOrWhiteSpace(graphId))
                    throw new Exception("Current Outlook message not found");

                // STEP 2: Reply
                var payload = new
                {
                    message = new
                    {
                        toRecipients = new[]
                        {
                    new { emailAddress = new { address = replyToEmail } }
                },
                        body = new
                        {
                            contentType = "HTML",
                            content = finalBody
                        }
                    }
                };

                var safeId = Uri.EscapeDataString(graphId);

                var response = await client.PostAsJsonAsync(
                    $"https://graph.microsoft.com/v1.0/me/messages/{safeId}/reply",
                    payload);

                var result = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                    throw new Exception(result);

                // SAVE LOG
                _context.EmailLogs.Add(new EmailLog
                {
                    ClientId = clientId,
                    ContactId = firstLog?.ContactId ?? firstInbox?.Contactid ?? firstReply?.ContactId,
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
                    MessageId = normalizedMsgId,
                    ThreadId = lastSent?.ThreadId ?? firstInbox?.ThreadId ?? firstReply?.ThreadId,
                    process_name = "ThreadReply"
                });

                await _context.SaveChangesAsync();

                return new EmailSendResult
                {
                    Success = true,
                    Message = "Reply sent in SAME Outlook conversation ✅"
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
    }
}
