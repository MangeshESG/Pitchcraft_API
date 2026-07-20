using Microsoft.EntityFrameworkCore;
using MimeKit;
using PitchGenApi.Database;
using PitchGenApi.Interfaces;
using PitchGenApi.Model.DTOs;
using System.Net.Http.Headers;

namespace PitchGenApi.Repository
{
    public class ForwardRepository : IForwardRepository
    {
        private readonly AppDbContext _context;
        private readonly IInboxRepository _inboxRepository;
        private readonly EmailSendingHelper _emailSending;

        public ForwardRepository( AppDbContext context, IInboxRepository inboxRepository, EmailSendingHelper emailSending)
        {
            _context = context;
            _inboxRepository = inboxRepository;
            _emailSending = emailSending;
        }
        public async Task<EmailSendResult> ForwardEmailUsingSmtp(Guid trackingid, int clientId,string forwardToEmail, string forwardMessage, int outboxId, string? BccEmail = "")
        {
            try
            {
                // =========================
                // GET INBOX + SMTP
                // =========================
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

                // =========================
                // GET ORIGINAL EMAIL
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

                // =========================
                // SUBJECT
                // =========================
                var originalSubject =
                    firstInbox?.Subject ??
                    firstReply?.Subject ??
                    firstLog?.Subject ??
                    "Forwarded Email";

                var forwardSubject = originalSubject.StartsWith("Fwd:",
                        StringComparison.OrdinalIgnoreCase)
                    ? originalSubject
                    : $"Fwd: {originalSubject}";

                // =========================
                // ORIGINAL BODY
                // =========================
                var originalBody =
                    firstInbox?.Body ??
                    firstReply?.Body ??
                    firstLog?.Body ??
                    "";

                var originalFrom =
                    firstInbox?.FromEmail ??
                    firstReply?.FromEmail ??
                    firstLog?.ToEmail ??
                    "";

                var originalDate =
                    firstInbox?.CreatedAt ??
                    firstReply?.Date ??
                    firstLog?.SentAt;

                // =========================
                // FORWARD BODY
                // =========================
                string finalBody = $@"
            <div>
                {forwardMessage}
            </div>

            <br/>
            <br/>

            <hr/>

            <p><b>---------- Forwarded message ----------</b></p>

            <p>
                <b>From:</b> {originalFrom}<br/>
                <b>Date:</b> {originalDate}<br/>
                <b>Subject:</b> {originalSubject}<br/>
            </p>

            <br/>

            <div>
                {originalBody}
            </div>
        ";

                // =========================
                // CREATE MAIL
                // =========================
                var mail = new MimeMessage();

                mail.From.Add(new MailboxAddress(
                    smtpCredential.SenderName,
                    smtpCredential.FromEmail));

                mail.To.Add(MailboxAddress.Parse(forwardToEmail));

                if (!string.IsNullOrWhiteSpace(BccEmail))
                {
                    mail.Bcc.Add(MailboxAddress.Parse(BccEmail));
                }

                mail.Subject = forwardSubject;

                // IMPORTANT:
                // NO InReplyTo
                // NO References
                // NO Thread-Index

                var newMessageId =
                    $"<{MimeKit.Utils.MimeUtils.GenerateMessageId().Trim('<', '>')}>";

                mail.Headers.Replace(HeaderId.MessageId, newMessageId);

                // =========================
                // BODY
                // =========================
                var builder = new BodyBuilder
                {
                    HtmlBody = finalBody
                };

                mail.Body = builder.ToMessageBody();

                // =========================
                // SMTP SEND
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
                    ContactId = firstLog?.ContactId
                                ?? firstInbox?.Contactid
                                ?? firstReply?.ContactId,

                    ToEmail = forwardToEmail,
                    Subject = mail.Subject,
                    Body = finalBody,

                    SenderEmailId = smtpCredential.FromEmail,
                    EmailSenderName = smtpCredential.SenderName,

                    Provider = "SMTP",
                    outboxid = smtpCredential.Id,

                    IsSuccess = true,
                    SentAt = DateTime.UtcNow,

                    TrackingId = trackingid, // NEW THREAD
                    MessageId = newMessageId,

                    process_name = "ForwardEmail"
                });

                await _context.SaveChangesAsync();

                return new EmailSendResult
                {
                    Success = true,
                    Message = "Email forwarded successfully"
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


        public async Task<EmailSendResult> ForwardEmailUsingGmailApi(Guid trackingid, int clientId, string forwardToEmail, string forwardMessage, int outboxId, string? BccEmail = "")
        {
            try
            {
                var tokenData = await _emailSending.GetValidGmailTokenAsync(outboxId);

                if (tokenData == null)
                {
                    return new EmailSendResult
                    {
                        Success = false,
                        Message = "Gmail not connected"
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

                var originalSubject =
                    firstInbox?.Subject ??
                    firstReply?.Subject ??
                    firstLog?.Subject ??
                    "Forwarded Email";

                var forwardSubject = originalSubject.StartsWith(
                        "Fwd:",
                        StringComparison.OrdinalIgnoreCase)
                    ? originalSubject
                    : $"Fwd: {originalSubject}";

                var originalBody =
                    firstInbox?.Body ??
                    firstReply?.Body ??
                    firstLog?.Body ??
                    "";

                var originalFrom =
                    firstInbox?.FromEmail ??
                    firstReply?.FromEmail ??
                    firstLog?.ToEmail ??
                    "";

                var originalDate =
                    firstInbox?.CreatedAt ??
                    firstReply?.Date ??
                    firstLog?.SentAt;

                string finalBody = $@"
                    <div>
                        {forwardMessage}
                    </div>

                    <br/><br/>

                    <hr/>

                    <p><b>---------- Forwarded message ----------</b></p>

                    <p>
                        <b>From:</b> {originalFrom}<br/>
                        <b>Date:</b> {originalDate}<br/>
                        <b>Subject:</b> {originalSubject}<br/>
                    </p>

                    <br/>

                    <div>
                        {originalBody}
                    </div>";

                var mimeMessage = new MimeMessage();

                mimeMessage.From.Add(new MailboxAddress(tokenData.SenderName, tokenData.Email));
                mimeMessage.To.Add(MailboxAddress.Parse(forwardToEmail));

                if (!string.IsNullOrWhiteSpace(BccEmail))
                {
                    foreach (var bcc in BccEmail.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var email = bcc.Trim();
                        if (!string.IsNullOrWhiteSpace(email))
                        {
                            mimeMessage.Bcc.Add(MailboxAddress.Parse(email));
                        }
                    }
                }

                mimeMessage.Subject = forwardSubject;
                mimeMessage.Headers.Add("Message-ID", MimeKit.Utils.MimeUtils.GenerateMessageId());

                var bodyBuilder = new BodyBuilder { HtmlBody = finalBody };
                mimeMessage.Body = bodyBuilder.ToMessageBody();
                mimeMessage.Body.ContentType.Charset = "utf-8";

                using var ms = new MemoryStream();
                await mimeMessage.WriteToAsync(ms);

                var rawMessage = Convert.ToBase64String(ms.ToArray())
                    .Replace('+', '-')
                    .Replace('/', '_')
                    .Replace("=", "");

                using var client = new HttpClient();
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", tokenData.AccessToken);

                var response = await client.PostAsJsonAsync(
                    "https://gmail.googleapis.com/gmail/v1/users/me/messages/send",
                    new { raw = rawMessage });

                var resultJson = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                    throw new Exception(resultJson);

                dynamic gmailResponse = Newtonsoft.Json.JsonConvert.DeserializeObject(resultJson);
                string gmailMessageId = gmailResponse?.id ?? MimeKit.Utils.MimeUtils.GenerateMessageId();
                string gmailThreadId = gmailResponse?.threadId;

                _context.EmailLogs.Add(new EmailLog
                {
                    ClientId = clientId,
                    ContactId = firstLog?.ContactId
                                ?? firstInbox?.Contactid
                                ?? firstReply?.ContactId,
                    ToEmail = forwardToEmail,
                    Subject = forwardSubject,
                    Body = finalBody,
                    SenderEmailId = tokenData.Email,
                    EmailSenderName = tokenData.SenderName,
                    Provider = "Gmail",
                    outboxid = tokenData.Id,
                    IsSuccess = true,
                    SentAt = DateTime.UtcNow,
                    TrackingId = trackingid,
                    MessageId = gmailMessageId,
                    ThreadId = gmailThreadId,
                    process_name = "ForwardEmail"
                });

                await _context.SaveChangesAsync();

                return new EmailSendResult
                {
                    Success = true,
                    Message = "Email forwarded successfully"
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
        public async Task<EmailSendResult> ForwardEmailUsingOutlookApi(Guid trackingid, int clientId, string forwardToEmail, string forwardMessage, int outboxId, string BccEmail = "")

        {
            try
            {
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
                // ORIGINAL EMAIL
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

                // =========================
                // SUBJECT
                // =========================
                var originalSubject =
                    firstInbox?.Subject ??
                    firstReply?.Subject ??
                    firstLog?.Subject ??
                    "Forwarded Email";

                var forwardSubject = originalSubject.StartsWith(
                        "Fwd:",
                        StringComparison.OrdinalIgnoreCase)
                    ? originalSubject
                    : $"Fwd: {originalSubject}";

                // =========================
                // BODY
                // =========================
                var originalBody =
                    firstInbox?.Body ??
                    firstReply?.Body ??
                    firstLog?.Body ??
                    "";

                var originalFrom =
                    firstInbox?.FromEmail ??
                    firstReply?.FromEmail ??
                    firstLog?.ToEmail ??
                    "";

                var originalDate =
                    firstInbox?.CreatedAt ??
                    firstReply?.Date ??
                    firstLog?.SentAt;

                string finalBody = $@"
                    <div>
                        {forwardMessage}
                    </div>

                    <br/><br/>

                    <hr/>

                    <p><b>---------- Forwarded message ----------</b></p>

                    <p>
                        <b>From:</b> {originalFrom}<br/>
                        <b>Date:</b> {originalDate}<br/>
                        <b>Subject:</b> {originalSubject}<br/>
                    </p>

                    <br/>

                    <div>
                        {originalBody}
                    </div>";

                // =========================
                // SEND VIA GRAPH API
                // =========================
                using var client = new HttpClient();

                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue(
                        "Bearer",
                        tokenData.AccessToken);

                // ? Build message dynamically
                var message = new Dictionary<string, object>
                {
                    ["subject"] = forwardSubject,

                    ["body"] = new
                    {
                        contentType = "HTML",
                        content = finalBody
                    },

                    ["toRecipients"] = new[]
                    {
                        new
                        {
                            emailAddress = new
                            {
                                address = forwardToEmail
                            }
                        }
                    }
                };

                // ? Add BCC only if exists
                if (!string.IsNullOrWhiteSpace(BccEmail))
                {
                    message["bccRecipients"] = new[]
                    {
                        new
                        {
                            emailAddress = new
                            {
                                address = BccEmail
                            }
                        }
                    };
                }

                var payload = new
                {
                    message,
                    saveToSentItems = true
                };

                var response = await client.PostAsJsonAsync(
                    "https://graph.microsoft.com/v1.0/me/sendMail",
                    payload);

                var result = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                    throw new Exception(result);

                // =========================
                // SAVE LOG
                // =========================
                _context.EmailLogs.Add(new EmailLog
                {
                    ClientId = clientId,

                    ContactId = firstLog?.ContactId
                                ?? firstInbox?.Contactid
                                ?? firstReply?.ContactId,

                    ToEmail = forwardToEmail,

                    Subject = forwardSubject,

                    Body = finalBody,

                    SenderEmailId = tokenData.Email,

                    EmailSenderName = tokenData.SenderName,

                    Provider = "Outlook",

                    outboxid = tokenData.Id,

                    IsSuccess = true,

                    SentAt = DateTime.UtcNow,

                    TrackingId = trackingid,

                    process_name = "ForwardEmail"
                });

                await _context.SaveChangesAsync();

                return new EmailSendResult
                {
                    Success = true,
                    Message = "Email forwarded successfully"
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

