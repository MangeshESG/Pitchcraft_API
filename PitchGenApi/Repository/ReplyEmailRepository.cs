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
            var inbox = await _context.Inboxcredentials
                .FirstOrDefaultAsync(x => x.Id == outboxId && x.ClientId == clientId);

            var smtpCredential = await _context.SmtpCredentials
                .FirstOrDefaultAsync(x => x.Id == inbox.Outboxid);

            var user = await _context.ClientDetails
                .FirstOrDefaultAsync(x => x.Id == clientId);

            // 🔥 STEP 1: get latest message from BOTH tables
            var lastSent = await _context.EmailLogs
                .Where(x => x.TrackingId == trackingid)
                .OrderByDescending(x => x.SentAt)
                .FirstOrDefaultAsync();

            var lastReply = await _context.EmailReplies
                .Where(x => x.TrackingId == trackingid)
                .OrderByDescending(x => x.Date)
                .FirstOrDefaultAsync();

            // 🔥 choose latest message (VERY IMPORTANT)
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
            else if (lastSent != null)
            {
                lastMessageId = lastSent.MessageId;
            }

            string newMessageId = MimeUtils.GenerateMessageId();

            try
            {
                using var smtpClient = new MailKit.Net.Smtp.SmtpClient();

                var socketOption = _inboxRepository.GetSecureOption(smtpCredential.SecurityType);

                await smtpClient.ConnectAsync(
                    smtpCredential.Server,
                    smtpCredential.Port,
                    socketOption);

                await smtpClient.AuthenticateAsync(
                    smtpCredential.Username,
                    smtpCredential.Password);

                string finalBody = replyBody;

                // tracking
                if (user.IsTracking)
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

                // 🔥 MIME MESSAGE
                var mail = new MimeMessage();

                mail.From.Add(
                    new MailboxAddress(
                        smtpCredential.SenderName,
                        smtpCredential.FromEmail));

                mail.To.Add(
                    MailboxAddress.Parse(lastSent.ToEmail));

                mail.Subject =
                    lastSent?.Subject?.StartsWith("Re:") == true
                        ? lastSent.Subject
                        : "Re: " + lastSent?.Subject;

                // NEW MESSAGE ID
                mail.MessageId = newMessageId;

                // THREADING FIX
                if (!string.IsNullOrWhiteSpace(lastMessageId))
                {
                    mail.InReplyTo = lastMessageId;

                    var allMessageIds = await _context.EmailLogs
                        .Where(x => x.TrackingId == trackingid)
                        .Select(x => x.MessageId)
                        .Where(x => !string.IsNullOrEmpty(x))
                        .ToListAsync();

                    var replyIds = await _context.EmailReplies
                        .Where(x => x.TrackingId == trackingid)
                        .Select(x => x.MessageId)
                        .Where(x => !string.IsNullOrEmpty(x))
                        .ToListAsync();

                    foreach (var id in allMessageIds.Concat(replyIds).Distinct())
                    {
                        mail.References.Add(id);
                    }
                }

                mail.Body = new BodyBuilder
                {
                    HtmlBody = finalBody
                }.ToMessageBody();

                await smtpClient.SendAsync(mail);

                if (smtpClient.IsConnected)
                    await smtpClient.DisconnectAsync(true);

                // SAVE LOG
                _context.EmailLogs.Add(new EmailLog
                {
                    ClientId = clientId,
                    ContactId = lastSent.ContactId,
                    CampaignId = lastSent.CampaignId,
                    BlueprintId = lastSent.BlueprintId,
                    SegmentId = lastSent.SegmentId,
                    ToEmail = lastSent.ToEmail,
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
                    process_name = "ThreadReply"
                });

                await _context.SaveChangesAsync();

                return new EmailSendResult
                {
                    Success = true,
                    Message = "Reply sent in SAME conversation ✅"
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

        public async Task<EmailSendResult> ReplyEmailUsingOutlookApi(
    Guid trackingid,
    int clientId,
    string replyBody,
    int outboxId,
    string BccEmail = "")
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

            // Last sent mail
            var lastSent = await _context.EmailLogs
                .Where(x => x.TrackingId == trackingid)
                .OrderByDescending(x => x.SentAt)
                .FirstOrDefaultAsync();

            // Last reply
            var lastReply = await _context.EmailReplies
                .Where(x => x.TrackingId == trackingid)
                .OrderByDescending(x => x.Date)
                .FirstOrDefaultAsync();

            if (lastSent == null)
            {
                return new EmailSendResult
                {
                    Success = false,
                    Message = "Original mail not found"
                };
            }

            // Latest message id in thread
            string lastMessageId;

            if (lastReply != null)
            {
                lastMessageId = lastReply.Date > lastSent.SentAt
                    ? lastReply.MessageId
                    : lastSent.MessageId;
            }
            else
            {
                lastMessageId = lastSent.MessageId;
            }

            if (string.IsNullOrWhiteSpace(lastMessageId))
            {
                return new EmailSendResult
                {
                    Success = false,
                    Message = "Thread MessageId not found"
                };
            }

            try
            {
                string finalBody = replyBody;

                // Tracking
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
                // Build payload
                // =========================
                object payload;

                if (!string.IsNullOrWhiteSpace(BccEmail))
                {
                    payload = new
                    {
                        message = new
                        {
                            body = new
                            {
                                contentType = "HTML",
                                content = finalBody
                            },

                            bccRecipients = new[]
                            {
                        new
                        {
                            emailAddress = new
                            {
                                address = BccEmail
                            }
                        }
                    },

                            internetMessageHeaders = new[]
                            {
                        new
                        {
                            name = "X-Tracking-Id",
                            value = trackingid.ToString()
                        }
                    }
                        }
                    };
                }
                else
                {
                    payload = new
                    {
                        message = new
                        {
                            body = new
                            {
                                contentType = "HTML",
                                content = finalBody
                            },

                            internetMessageHeaders = new[]
                            {
                        new
                        {
                            name = "X-Tracking-Id",
                            value = trackingid.ToString()
                        }
                    }
                        }
                    };
                }

                using var client = new HttpClient();
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue(
                        "Bearer",
                        tokenData.AccessToken);

                // SAME THREAD reply
                var response = await client.PostAsJsonAsync(
                    $"https://graph.microsoft.com/v1.0/me/messages/{lastMessageId}/reply",
                    payload);

                var result = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                    throw new Exception(result);

                // Save log
                string newMessageId = MimeKit.Utils.MimeUtils.GenerateMessageId();

                _context.EmailLogs.Add(new EmailLog
                {
                    ClientId = clientId,
                    ToEmail = lastSent.ToEmail,
                    Subject = lastSent.Subject.StartsWith("Re:")
                        ? lastSent.Subject
                        : "Re: " + lastSent.Subject,
                    Body = replyBody,
                    SenderEmailId = tokenData.Email,
                    EmailSenderName = tokenData.SenderName,
                    Provider = "Outlook",
                    outboxid = tokenData.Id,
                    IsSuccess = true,
                    SentAt = DateTime.UtcNow,
                    TrackingId = trackingid,
                    MessageId = newMessageId,
                    ThreadId = lastSent.ThreadId,
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
