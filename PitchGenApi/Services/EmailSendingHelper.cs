
using PitchGenApi.Database;
using Microsoft.EntityFrameworkCore;
using PitchGenApi.Model;
using PitchGenApi.Interfaces;
using PitchGenApi.Model.DTOs;
using MailKit.Security;
using MimeKit;
using MimeKit.Utils;
using System.Net.Http.Headers;
using Newtonsoft.Json;
using System.Text.RegularExpressions;

public class EmailSendingHelper
{
    private readonly AppDbContext _context;
    private readonly ContactRepository _repository;
    private readonly IDomainVerificationRepository _domain;
    private readonly IConfiguration _config;
    private readonly IInboxRepository _inboxRepository;
    private readonly IUnsubscribeRepository _unsubscribeRepository;
     
    public EmailSendingHelper(
        AppDbContext context,
        ContactRepository repository,
        IDomainVerificationRepository domain,
        IConfiguration config,
        IInboxRepository inboxRepository,
        IUnsubscribeRepository unsubscribeRepository)
    {
        _context = context;
        _repository = repository;
        _domain = domain;
        _config = config;
        _inboxRepository = inboxRepository;
        _unsubscribeRepository = unsubscribeRepository;
    }

    private async Task AddOneClickUnsubscribeHeadersAsync(
        MimeMessage message,
        ClientDetails? client,
        int clientId,
        int contactId,
        string email,
        bool isContactCompose)
    {
        if (isContactCompose || client?.IsRfcUnsubscribeAllowed != true)
        {
            return;
        }

        var unsubscribeLink = await _unsubscribeRepository.GenerateOneClickUnsubscribeLinkAsync(
            string.IsNullOrWhiteSpace(client.CompanyName) ? "Pitchkraft" : client.CompanyName,
            clientId,
            contactId,
            email);

        if (!Uri.TryCreate(unsubscribeLink, UriKind.Absolute, out var unsubscribeUri)
            || unsubscribeUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("One-click unsubscribe link must be an HTTPS URL.");
        }

        message.Headers.Add("List-Unsubscribe", $"<{unsubscribeUri.AbsoluteUri}>");
        message.Headers.Add("List-Unsubscribe-Post", "List-Unsubscribe=One-Click");
    }

    private static List<string> NormalizeEmailList(IEnumerable<string>? emails)
    {
        return emails?
            .Where(email => !string.IsNullOrWhiteSpace(email))
            .Select(email => email.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? new List<string>();
    }

    private static List<string> NormalizeEmailList(string? emails)
    {
        return string.IsNullOrWhiteSpace(emails)
            ? new List<string>()
            : NormalizeEmailList(emails.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries));
    }
    /// <summary>
    /// Removes "Sourced from" highlight markup (background color, help cursor,
    /// tooltip) from the email HTML so prospects receive clean text.
    /// Unwraps the span but keeps its inner text.
    /// </summary>
    private static readonly Regex SpanOpenTagRegex = new(
        @"<span\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex StyleAttrRegex = new(
        @"style\s*=\s*(?<q>[""'])(?<val>[^""']*)\k<q>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // background, background-color and the help cursor that comes with them
    private static readonly Regex HighlightDeclRegex = new(
        @"(?:background(?:-color)?|cursor)\s*:[^;]*;?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // marks a span as generated, so hand-applied highlights are left alone
    private static readonly Regex GeneratedMarkerRegex = new(
        @"cursor\s*:\s*help|title\s*=\s*[""']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TooltipTitleRegex = new(
        @"\s+title\s*=\s*([""'])[^""']*\1",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string StripSourceHighlights(string html)
    {
        if (string.IsNullOrEmpty(html)) return html;

        const RegexOptions opts = RegexOptions.IgnoreCase;

        return Regex.Replace(html, @"<span\b[^>]*>", m =>
        {
            var tag = m.Value;

            // only touch generated highlight spans; hand-applied ones stay
            if (!Regex.IsMatch(tag, @"cursor\s*:\s*help|title\s*=\s*[""']", opts))
                return tag;

            // strip background/cursor declarations, keep the rest of the style
            tag = Regex.Replace(tag, @"style\s*=\s*([""'])([^""']*)\1", sm =>
            {
                var q = sm.Groups[1].Value;
                var cleaned = Regex.Replace(
                        sm.Groups[2].Value,
                        @"(?:background(?:-color)?|cursor)\s*:[^;]*;?", "", opts)
                    .Trim().Trim(';').Trim();
                return cleaned.Length == 0 ? "" : $"style={q}{cleaned}{q}";
            }, opts);

            tag = Regex.Replace(tag, @"\s+title\s*=\s*([""'])[^""']*\1", "", opts);
            tag = Regex.Replace(tag, @"\s{2,}", " ");
            return Regex.Replace(tag, @"\s+>", ">");
        }, opts);
    }




    public Task<EmailSendResult> SendEmailUsingSmtp(int clientId, int contactId, int? CampaignId, bool isFollowUp, string BccEmail = "", int SmtpID = 0)
    {
        return SendEmailUsingSmtp(clientId, contactId, CampaignId, isFollowUp, null, NormalizeEmailList(BccEmail), SmtpID);
    }
    public async Task<EmailSendResult> SendEmailUsingSmtp(int clientId, int contactId, int? CampaignId,bool isFollowUp, List<string>? CcEmail = null, List<string>? BccEmail = null, int SmtpID = 0, bool isContactCompose = false)
    {
        var EmailDetails = await _context.contacts.FirstOrDefaultAsync(x => x.id == contactId);


        var smtpCredential = await _context.SmtpCredentials.FirstOrDefaultAsync(x => x.Id == SmtpID);

        var Blueprint = CampaignId.HasValue
            ? await _context.Campaigns
                .FirstOrDefaultAsync(x => x.Id == CampaignId.Value)
            : null;

        int DataFileId = 0;

        if (!string.IsNullOrWhiteSpace(Blueprint?.ZohoViewId))
        {
            int.TryParse(Blueprint.ZohoViewId, out DataFileId);
        }

        int? blueprintId = Blueprint?.TemplateId;
        int? segmentId = Blueprint?.SegmentId;


        if (string.IsNullOrWhiteSpace(EmailDetails.email_subject) ||
                 EmailDetails.email_subject.Trim().ToUpper() == "N/A" ||
                 string.IsNullOrWhiteSpace(EmailDetails.email_body) ||
                 EmailDetails.email_body.Trim().ToUpper() == "N/A")
        {
            return new EmailSendResult
            {
                Success = false,
                Message = "Email body or subject is incorrect."
            };
        }

        bool active = await _context.UserCredits
                .AnyAsync(u =>
                    u.ClientId == clientId &&
                    u.Status == "active"
                );


        if (smtpCredential == null || string.IsNullOrEmpty(smtpCredential.Server))
        {
            _context.EmailLogs.Add(new EmailLog
            {
                ClientId = clientId,
                ContactId = contactId,
                DataFileId = DataFileId,
                CampaignId = CampaignId,
                BlueprintId = blueprintId,
                SegmentId = segmentId,
                ToEmail = EmailDetails.email,
                Subject = EmailDetails.email_subject,
                Body = EmailDetails.email_body,
                IsSuccess = false,
                ErrorMessage = "SMTP credentials not found or invalid.",
                zohoViewName = "from pichkraft",
                SentAt = DateTime.UtcNow
            });
            await _context.SaveChangesAsync();

            return new EmailSendResult
            {
                Success = false,
                Message = "SMTP credentials not found or invalid."
            };
        }

        var user = await _context.ClientDetails
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == clientId);

        //bool isVerified = await _domain.IsSmtpFullyVerifiedAsync(SmtpID);

        string smtpServer = smtpCredential.Server;
        int smtpPort = smtpCredential.Port;
        string smtpUsername = smtpCredential.Username;
        string smtpPassword = smtpCredential.Password;

        string fromEmailToUse = smtpCredential.FromEmail;
        string senderName = smtpCredential.SenderName;

        MailKit.Net.Smtp.SmtpClient smtpClient = new();

        try
        {

            //if (!isVerified)
            //{
            //    smtpServer = "mail.sender.pitchkraft.ai";
            //    smtpPort = 587;
            //    useSsl = true;
            //    smtpUsername = "message-service@sender.pitchkraft.ai";
            //    smtpPassword = "yV%691jd9";

            //    fromEmailToUse = "message-service@sender.pitchkraft.ai";
            //}
            string trackingId = Guid.NewGuid().ToString();

            string rawMessageId = MimeUtils.GenerateMessageId();
            string messageId = $"<{rawMessageId.Trim('<', '>')}>"; // normalize with <>

            string threadIndex = Convert.ToBase64String(Guid.NewGuid().ToByteArray());

            var socketOption = _inboxRepository.GetSecureOption(smtpCredential.SecurityType);


            await smtpClient.ConnectAsync(smtpServer, smtpPort, socketOption);
            await smtpClient.AuthenticateAsync(smtpUsername, smtpPassword);

            string finalEmailBody = EmailDetails.email_body;
            
            string emailFooter = @"<br/><br/>
                <hr style='border:none;border-top:1px solid #e5e7eb;'/>
                <p style='font-size:12px;color:#6b7280;text-align:center;'>
                This message was sent from 
                <a href='https://app.pitchkraft.ai/' target='_blank' style='color:#2563eb;text-decoration:none;font-weight:bold;'>
                Pitchkraft.ai
                </a>
                </p>";


               //var list = await _context.segments
               //     .FirstOrDefaultAsync(s => s.Id == Blueprint.SegmentId && s.ClientId == clientId);

                


                if (isFollowUp)
                {
                    string oldThread = await _repository.BuildEmailThreadAsync(clientId, DataFileId, EmailDetails.id, segmentId);

                    finalEmailBody =
                     $@"{EmailDetails.email_body}

                    {oldThread}
                    {emailFooter}";

                }



            if (!active)
            {
                finalEmailBody += emailFooter;
            }

            // Remove source-highlight markup before the prospect sees it
            finalEmailBody = StripSourceHighlights(finalEmailBody);

            string untrackedEmailBody = finalEmailBody;


            // ?? STEP 1: Hidden tracking inject (reply ke liye)
            finalEmailBody = EmailTrackingHelper.InjectinboxTracking(finalEmailBody, trackingId);

            // Send main email
            if (!string.IsNullOrWhiteSpace(EmailDetails.email))
            {
                if (user.IsTracking)
                {
                    finalEmailBody = EmailTrackingHelper.InjectClickTracking(finalEmailBody, trackingId);
                    finalEmailBody += EmailTrackingHelper.GetPixelTag(trackingId);
                }

                var toMessage = new MimeMessage();
                var ccEmailList = NormalizeEmailList(CcEmail);
                var bccEmailList = NormalizeEmailList(BccEmail);
                var envelopeRecipients = new List<MailboxAddress>();

                var primaryRecipient = MailboxAddress.Parse(EmailDetails.email);
                envelopeRecipients.Add(primaryRecipient);

                toMessage.From.Add(new MailboxAddress(senderName, fromEmailToUse));
                toMessage.To.Add(primaryRecipient);
                foreach (var cc in ccEmailList)
                {
                    var ccRecipient = MailboxAddress.Parse(cc);
                    toMessage.Cc.Add(ccRecipient);
                    envelopeRecipients.Add(ccRecipient);
                }
                foreach (var bcc in bccEmailList)
                {
                    var bccRecipient = MailboxAddress.Parse(bcc);
                    toMessage.Bcc.Add(bccRecipient);
                    envelopeRecipients.Add(bccRecipient);
                }
                toMessage.Subject = EmailDetails.email_subject;
                await AddOneClickUnsubscribeHeadersAsync(
                    toMessage, user, clientId, contactId, EmailDetails.email, isContactCompose);

                toMessage.Headers.Replace(HeaderId.MessageId, messageId); // keep <> brackets

                toMessage.Headers.Add("Thread-Index", threadIndex);
                toMessage.Headers.Add("Thread-Topic", toMessage.Subject);

                toMessage.Body = new BodyBuilder
                {
                    HtmlBody = finalEmailBody
                }.ToMessageBody();

                try
                {
                    await smtpClient.SendAsync(toMessage, (MailboxAddress)toMessage.From.Mailboxes.First(), envelopeRecipients);
                }
                catch (MailKit.Net.Smtp.SmtpCommandException ex)
                {
                    throw new Exception(
                        $"Main email failed. " +
                        $"Recipient: {EmailDetails.email}, " +
                        $"StatusCode: {ex.StatusCode}, " +
                        $"ErrorCode: {ex.ErrorCode}, " +
                        $"Mailbox: {ex.Mailbox?.Address}, " +
                        $"Message: {ex.Message}",
                        ex);
                }
                _context.EmailLogs.Add(new EmailLog
                {
                    ClientId = clientId,
                    ContactId = contactId,
                    CampaignId = CampaignId,
                    BlueprintId = blueprintId,
                    ToEmail = EmailDetails.email,
                    Subject = EmailDetails.email_subject,
                    Body = EmailDetails.email_body,
                    EmailRecipientName = EmailDetails.full_name,
                    EmailSenderName = senderName,
                    SenderEmailId = fromEmailToUse,
                    zohoViewName = "from pitch craft",
                    DataFileId = DataFileId,
                    Provider = "SMTP",
                    outboxid = smtpCredential.Id,
                    SegmentId = segmentId,
                    IsSuccess = true,
                    SentAt = DateTime.UtcNow,
                    TrackingId = Guid.Parse(trackingId),
                    MessageId = messageId,
                    process_name = "Single",
                    ThreadId = threadIndex
                });
            }

            if (smtpClient.IsConnected)
            {
                await smtpClient.DisconnectAsync(true);
            }

            await _context.SaveChangesAsync();

            return new EmailSendResult
            {
                Success = true,
                Message = $"Email sent successfully to {EmailDetails.email}.",
            };
        }
        catch (Exception ex)
        {
            _context.EmailLogs.Add(new EmailLog
            {
                ClientId = clientId,
                ContactId = contactId,
                CampaignId = CampaignId,
                BlueprintId = blueprintId,
                ToEmail = EmailDetails.email,
                Subject = EmailDetails.email_subject,
                Body = EmailDetails.email_body,
                EmailRecipientName = EmailDetails.full_name,
                EmailSenderName = senderName,
                SenderEmailId = fromEmailToUse,
                IsSuccess = false,
                ErrorMessage = ex.Message,
                Provider = "SMTP",
                outboxid = smtpCredential.Id,
                zohoViewName = "from pitch craft",
                DataFileId = DataFileId,
                SegmentId = segmentId,
                SentAt = DateTime.UtcNow,
                process_name = "Single"
            });

            if (smtpClient.IsConnected)
            {
                await smtpClient.DisconnectAsync(true);
            }

            smtpClient.Dispose();

            await _context.SaveChangesAsync();

            return new EmailSendResult
            {
                Success = false,
                Message = ex.Message
            };
        }
    }

    public Task<EmailSendResult> SendEmailUsingGmailApi(int clientId, int contactId, int? CampaignId, bool isFollowUp, string BccEmail = "", int OutBoxId = 0)
    {
        return SendEmailUsingGmailApi(clientId, contactId, CampaignId, isFollowUp, null, NormalizeEmailList(BccEmail), OutBoxId);
    }

    public async Task<EmailSendResult> SendEmailUsingGmailApi(
    int clientId, int contactId, int? CampaignId, bool isFollowUp, List<string>? CcEmail = null, List<string>? BccEmail = null, int OutBoxId = 0, bool isContactCompose = false)
    {
        var EmailDetails = await _context.contacts.FirstOrDefaultAsync(x => x.id == contactId);
        var Blueprint = CampaignId.HasValue
            ? await _context.Campaigns
                .FirstOrDefaultAsync(x => x.Id == CampaignId.Value)
            : null;

        int DataFileId = 0;

        if (!string.IsNullOrWhiteSpace(Blueprint?.ZohoViewId))
        {
            int.TryParse(Blueprint.ZohoViewId, out DataFileId);
        }

        int? blueprintId = Blueprint?.TemplateId;
        int? segmentId = Blueprint?.SegmentId;
        if (string.IsNullOrWhiteSpace(EmailDetails?.email_subject) ||
            string.IsNullOrWhiteSpace(EmailDetails?.email_body))
        {
            return new EmailSendResult
            {
                Success = false,
                Message = "Email body or subject is incorrect."
            };
        }

        var user = await _context.ClientDetails
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == clientId);

        var tokenData = await GetValidGmailTokenAsync(OutBoxId);
        if (tokenData == null)
        {
            return new EmailSendResult
            {
                Success = false,
                Message = "Gmail not connected."
            };
        }

        try
        {
            string trackingId = Guid.NewGuid().ToString();
            string customMessageId = MimeUtils.GenerateMessageId(); // header only

            string finalEmailBody = EmailDetails.email_body;

            string emailFooter = @"<br/><br/>
        <hr style='border:none;border-top:1px solid #e5e7eb;'/>
        <p style='font-size:12px;color:#6b7280;text-align:center;'>
        This message was sent from 
        <a href='https://app.pitchkraft.ai/' target='_blank'>Pitchkraft.ai</a>
        </p>";

            if (isFollowUp)
            {
                string oldThread = await _repository.BuildEmailThreadAsync(
                    clientId, DataFileId, EmailDetails.id, segmentId);

                finalEmailBody = $"{EmailDetails.email_body}{oldThread}{emailFooter}";
            }

            // Remove source-highlight markup before the prospect sees it
            finalEmailBody = StripSourceHighlights(finalEmailBody);

            string untrackedEmailBody = finalEmailBody;

            // ?? TRACKING
            finalEmailBody = EmailTrackingHelper.InjectinboxTracking(finalEmailBody, trackingId);


            if (user.IsTracking)
            {
                finalEmailBody = EmailTrackingHelper.InjectClickTracking(finalEmailBody, trackingId);
                finalEmailBody += EmailTrackingHelper.GetPixelTag(trackingId);
            }

            // =========================
            // MIME
            // =========================
            var mimeMessage = new MimeMessage();

            mimeMessage.From.Add(new MailboxAddress(tokenData.SenderName, tokenData.Email));
            mimeMessage.To.Add(new MailboxAddress("", EmailDetails.email));
            foreach (var cc in NormalizeEmailList(CcEmail))
            {
                mimeMessage.Cc.Add(new MailboxAddress("", cc));
            }
            foreach (var bcc in NormalizeEmailList(BccEmail))
            {
                mimeMessage.Bcc.Add(new MailboxAddress("", bcc));
            }
            mimeMessage.Subject = EmailDetails.email_subject;
            await AddOneClickUnsubscribeHeadersAsync(
                mimeMessage, user, clientId, contactId, EmailDetails.email, isContactCompose);

            mimeMessage.Headers.Add("Message-ID", customMessageId);
            mimeMessage.Headers.Add("X-Tracking-Id", trackingId);

            var bodyBuilder = new BodyBuilder { HtmlBody = finalEmailBody };
            mimeMessage.Body = bodyBuilder.ToMessageBody();
            mimeMessage.Body.ContentType.Charset = "utf-8";

            using var ms = new MemoryStream();
            await mimeMessage.WriteToAsync(ms);

            var rawMessage = Convert.ToBase64String(ms.ToArray())
                .Replace('+', '-')
                .Replace('/', '_')
                .Replace("=", "");

            var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", tokenData.AccessToken);

            var payload = new { raw = rawMessage };

            var response = await client.PostAsJsonAsync(
                "https://gmail.googleapis.com/gmail/v1/users/me/messages/send",
                payload);

            var resultJson = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                throw new Exception(resultJson);

            // ?? IMPORTANT: Gmail response parse
            var gmailResponse = JsonConvert.DeserializeObject<dynamic>(resultJson);

            string gmailMessageId = gmailResponse.id;
            string threadId = gmailResponse.threadId;

            // =========================
            // SAVE LOG (FIXED)
            // =========================
            _context.EmailLogs.Add(new EmailLog
            {
                ClientId = clientId,
                outboxid = tokenData.Id,
                ContactId = contactId,
                CampaignId = CampaignId,
                BlueprintId = blueprintId,
                ToEmail = EmailDetails.email,
                Subject = EmailDetails.email_subject,
                Body = EmailDetails.email_body,
                EmailRecipientName = EmailDetails.full_name,
                EmailSenderName = tokenData.SenderName,
                SenderEmailId = tokenData.Email,
                DataFileId = DataFileId,
                Provider = "Gmail",
                SegmentId = segmentId,
                IsSuccess = true,
                SentAt = DateTime.UtcNow,
                TrackingId = Guid.Parse(trackingId),

                // ?? FIXED PART
                MessageId = gmailMessageId,   // Gmail ka actual id
                ThreadId = threadId,          // ?? MUST SAVE

                process_name = "Single"
            });

            await _context.SaveChangesAsync();

            return new EmailSendResult
            {
                Success = true,
                Message = $"Email sent via Gmail API to {EmailDetails.email}"
            };
        }
        catch (Exception ex)
        {
            _context.EmailLogs.Add(new EmailLog
            {
                ClientId = clientId,
                ContactId = contactId,
                CampaignId = CampaignId,
                ToEmail = EmailDetails?.email,
                Subject = EmailDetails?.email_subject,
                Body = EmailDetails?.email_body,
                IsSuccess = false,
                ErrorMessage = ex.Message,
                SentAt = DateTime.UtcNow
            });

            await _context.SaveChangesAsync();
            return new EmailSendResult
            {
                Success = false,
                Message = ex.Message
            };
        }

    }

    public Task<EmailSendResult> SendEmailUsingOutlookApi(
    int clientId,
    int contactId,
    int? CampaignId,
    bool isFollowUp,
    string BccEmail = "",
    int OutBoxId = 0)
    {
        return SendEmailUsingOutlookApi(clientId, contactId, CampaignId, isFollowUp, null, NormalizeEmailList(BccEmail), OutBoxId);
    }

    public async Task<EmailSendResult> SendEmailUsingOutlookApi(
    int clientId,
    int contactId,
    int? CampaignId,
    bool isFollowUp,
    List<string>? CcEmail = null,
    List<string>? BccEmail = null,
    int OutBoxId = 0,
    bool isContactCompose = false)
    {
        var EmailDetails = await _context.contacts
            .FirstOrDefaultAsync(x => x.id == contactId);

        var Blueprint = CampaignId.HasValue
             ? await _context.Campaigns
                 .FirstOrDefaultAsync(x => x.Id == CampaignId.Value)
             : null;

        int DataFileId = 0;

        if (!string.IsNullOrWhiteSpace(Blueprint?.ZohoViewId))
        {
            int.TryParse(Blueprint.ZohoViewId, out DataFileId);
        }

        int? blueprintId = Blueprint?.TemplateId;
        int? segmentId = Blueprint?.SegmentId;
        if (string.IsNullOrWhiteSpace(EmailDetails?.email_subject) ||
            string.IsNullOrWhiteSpace(EmailDetails?.email_body))
        {
            return new EmailSendResult
            {
                Success = false,
                Message = "Email body or subject is incorrect."
            };
        }

        var user = await _context.ClientDetails
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == clientId);

        var tokenData = await GetValidOutlookTokenAsync(OutBoxId);

        if (tokenData == null)
        {
            return new EmailSendResult
            {
                Success = false,
                Message = "Outlook not connected."
            };
        }

        try
        {
            string trackingId = Guid.NewGuid().ToString();

            string finalEmailBody = EmailDetails.email_body;

            if (isFollowUp)
            {
                string oldThread = await _repository.BuildEmailThreadAsync(
                    clientId,
                    DataFileId,
                    EmailDetails.id,
                    segmentId);

                finalEmailBody = $"{EmailDetails.email_body}{oldThread}";
            }

            // Remove source-highlight markup before the prospect sees it
            finalEmailBody = StripSourceHighlights(finalEmailBody);

            string untrackedEmailBody = finalEmailBody;

            finalEmailBody = EmailTrackingHelper.InjectinboxTracking(
                finalEmailBody,
                trackingId);


            if (user?.IsTracking == true)
            {
                finalEmailBody = EmailTrackingHelper.InjectClickTracking(
                    finalEmailBody,
                    trackingId);

                finalEmailBody += EmailTrackingHelper.GetPixelTag(trackingId);
            }

            bool useRfcUnsubscribe = !isContactCompose && user?.IsRfcUnsubscribeAllowed == true;

            // Both routes use the same MIME content; RFC-enabled sends require SMTP.
            var mimeMessage = new MimeMessage();
            mimeMessage.From.Add(new MailboxAddress(tokenData.SenderName, tokenData.Email));
            mimeMessage.To.Add(new MailboxAddress("", EmailDetails.email));

            foreach (var cc in NormalizeEmailList(CcEmail))
            {
                mimeMessage.Cc.Add(new MailboxAddress("", cc));
            }

            foreach (var bcc in NormalizeEmailList(BccEmail))
            {
                mimeMessage.Bcc.Add(new MailboxAddress("", bcc));
            }

            mimeMessage.Subject = EmailDetails.email_subject;
            mimeMessage.MessageId = MimeUtils.GenerateMessageId();
            mimeMessage.Headers.Add("X-Tracking-Id", trackingId);

            await AddOneClickUnsubscribeHeadersAsync(
                mimeMessage,
                user,
                clientId,
                contactId,
                EmailDetails.email,
                isContactCompose);

            mimeMessage.Body = new TextPart("html") { Text = finalEmailBody };

            string internetMessageId;
            string threadId;

            if (useRfcUnsubscribe)
            {
                internetMessageId = $"<{mimeMessage.MessageId.Trim('<', '>')}>";
                threadId = internetMessageId;
                string smtpAccessToken = await GetOutlookSmtpAccessTokenAsync(
                    tokenData.RefreshToken, tokenData.Email);

                using var smtpClient = new MailKit.Net.Smtp.SmtpClient();
                await smtpClient.ConnectAsync(
                    "smtp.office365.com", 587, SecureSocketOptions.StartTls);
                await smtpClient.AuthenticateAsync(
                    new SaslMechanismOAuth2(tokenData.Email, smtpAccessToken));
                await smtpClient.SendAsync(mimeMessage);
                await smtpClient.DisconnectAsync(true);
            }
            else
            {
                // Use the original Graph draft/send flow when RFC headers aren't needed.
                using var draftStream = new MemoryStream();
                await mimeMessage.WriteToAsync(draftStream);
                var rawDraft = Convert.ToBase64String(draftStream.ToArray());

                using var client = new HttpClient();
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", tokenData.AccessToken);

                using var draftContent = new StringContent(
                    rawDraft,
                    System.Text.Encoding.UTF8,
                    "text/plain");
                using var createResponse = await client.PostAsync(
                    "https://graph.microsoft.com/v1.0/me/messages",
                    draftContent);

                var createResult = await createResponse.Content.ReadAsStringAsync();
                if (!createResponse.IsSuccessStatusCode)
                    throw new Exception(createResult);

                dynamic createdMail = Newtonsoft.Json.JsonConvert.DeserializeObject(createResult);
                string graphMessageId = createdMail?.id?.ToString();
                internetMessageId = createdMail?.internetMessageId?.ToString();

                if (string.IsNullOrWhiteSpace(graphMessageId))
                    throw new Exception("Outlook draft id not returned");
                if (string.IsNullOrWhiteSpace(internetMessageId))
                    throw new Exception("internetMessageId not returned");

                using var sendResponse = await client.PostAsync(
                    $"https://graph.microsoft.com/v1.0/me/messages/{Uri.EscapeDataString(graphMessageId)}/send",
                    null);

                var sendResult = await sendResponse.Content.ReadAsStringAsync();
                if (!sendResponse.IsSuccessStatusCode)
                    throw new Exception(sendResult);

                threadId = graphMessageId;
            }

            // SAVE
            _context.EmailLogs.Add(new EmailLog
            {
                ClientId = clientId,
                outboxid = tokenData.Id,
                ContactId = contactId,
                CampaignId = CampaignId,
                BlueprintId = blueprintId,
                ToEmail = EmailDetails.email,
                Subject = EmailDetails.email_subject,
                Body = EmailDetails.email_body,
                EmailRecipientName = EmailDetails.full_name,
                EmailSenderName = tokenData.SenderName,
                SenderEmailId = tokenData.Email,
                DataFileId = DataFileId,
                Provider = "Outlook",
                SegmentId = segmentId,
                IsSuccess = true,
                SentAt = DateTime.UtcNow,
                TrackingId = Guid.Parse(trackingId),

                // IMPORTANT
                MessageId = internetMessageId,

                // optional
                ThreadId = threadId,

                process_name = "Single"
            });

            await _context.SaveChangesAsync();

            return new EmailSendResult
            {
                Success = true,
                Message = $"Email sent via Outlook {(useRfcUnsubscribe ? "SMTP" : "API")} to {EmailDetails.email}"
            };
        }
        catch (Exception ex)
        {
            var sendError = ex.Message.Contains("5.7.139", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("SmtpClientAuthentication is disabled", StringComparison.OrdinalIgnoreCase)
                ? "Outlook SMTP AUTH is disabled. Ask your Microsoft 365 admin to enable Authenticated SMTP for this sender mailbox. If it is blocked by tenant settings or Security Defaults, the admin must review those settings. The email was not sent."
                : ex.Message;

            _context.EmailLogs.Add(new EmailLog
            {
                ClientId = clientId,
                ContactId = contactId,
                CampaignId = CampaignId,
                ToEmail = EmailDetails?.email,
                Subject = EmailDetails?.email_subject,
                Body = EmailDetails?.email_body,
                IsSuccess = false,
                ErrorMessage = ex.Message,
                SentAt = DateTime.UtcNow
            });

            await _context.SaveChangesAsync();

            return new EmailSendResult
            {
                Success = false,
                Message = sendError
            };
        }
    }
    public async Task<EmailOAuthTokens> GetValidGmailTokenAsync(int id)
    {
        var cfg = _config.GetSection("GoogleOAuth");

        var tokenData = await _context.EmailOAuthTokens
            .FirstOrDefaultAsync(x => x.Id == id);

        if (tokenData == null)
            return null;

        // ? Expiry check (2 min buffer)
        if (tokenData.ExpiryTime > DateTime.UtcNow.AddMinutes(2))
            return tokenData;

        // ?? Refresh token
        var client = new HttpClient();

        var requestData = new Dictionary<string, string>
    {
        { "client_id", cfg["ClientId"]},
        { "client_secret", cfg["ClientSecret"]},
        { "refresh_token", tokenData.RefreshToken },
        { "grant_type", "refresh_token" }
    };

        var response = await client.PostAsync(
            "https://oauth2.googleapis.com/token",
            new FormUrlEncodedContent(requestData));

        var json = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            throw new Exception("Token refresh failed: " + json);

        dynamic obj = Newtonsoft.Json.JsonConvert.DeserializeObject(json);

        // ? Update DB
        tokenData.AccessToken = obj.access_token;
        tokenData.ExpiryTime = DateTime.UtcNow.AddSeconds((int)obj.expires_in);

        await _context.SaveChangesAsync();

        return tokenData;
    }
    private async Task<string> GetOutlookSmtpAccessTokenAsync(string refreshToken, string email)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            throw new InvalidOperationException("Outlook must be reconnected before SMTP sending.");

        var cfg = _config.GetSection("MicrosoftOAuth");
        using var client = new HttpClient();
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = cfg["ClientId"],
            ["client_secret"] = cfg["ClientSecret"],
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token",
            ["scope"] = "https://outlook.office.com/SMTP.Send"
        });

        using var response = await client.PostAsync(
            $"https://login.microsoftonline.com/{cfg["TenantId"]}/oauth2/v2.0/token",
            content);
        if (!response.IsSuccessStatusCode)
        {
            var domain = email?.Split('@').LastOrDefault();
            var isPersonalOutlook = domain != null &&
                (domain.Equals("outlook.com", StringComparison.OrdinalIgnoreCase) ||
                 domain.Equals("hotmail.com", StringComparison.OrdinalIgnoreCase) ||
                 domain.Equals("live.com", StringComparison.OrdinalIgnoreCase) ||
                 domain.Equals("msn.com", StringComparison.OrdinalIgnoreCase));

            throw new InvalidOperationException(isPersonalOutlook
                ? "This personal Outlook.com account cannot send through SMTP yet. Pitchcraft must request separate SMTP consent from this account; Microsoft 365 admin consent does not apply. Email was not sent."
                : "Could not get an Outlook SMTP token. This mailbox must authorize SMTP.Send for Pitchcraft. Email was not sent.");
        }

        var json = await response.Content.ReadAsStringAsync();
        dynamic token = Newtonsoft.Json.JsonConvert.DeserializeObject(json);
        string accessToken = token?.access_token?.ToString();
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new InvalidOperationException("Outlook SMTP access token was missing.");

        return accessToken;
    }

    public async Task<EmailOAuthTokens> GetValidOutlookTokenAsync(int id)
    {
        var cfg = _config.GetSection("MicrosoftOAuth");

        var tokenData = await _context.EmailOAuthTokens
            .FirstOrDefaultAsync(x => x.Id == id);

        if (tokenData == null)
            return null;

        // ? Expiry check (2 min buffer)
        if (tokenData.ExpiryTime > DateTime.UtcNow.AddMinutes(2))
            return tokenData;

        try
        {
            var client = new HttpClient();

            var requestData = new Dictionary<string, string>
        {
            { "client_id", cfg["ClientId"] },
            { "client_secret", cfg["ClientSecret"] },
            { "refresh_token", tokenData.RefreshToken },
            { "grant_type", "refresh_token" },
            { "scope", "https://graph.microsoft.com/.default" } // ?? IMPORTANT
        };

            var response = await client.PostAsync(
                $"https://login.microsoftonline.com/{cfg["TenantId"]}/oauth2/v2.0/token",
                new FormUrlEncodedContent(requestData));

            var json = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                throw new Exception("Outlook token refresh failed: " + json);

            dynamic obj = Newtonsoft.Json.JsonConvert.DeserializeObject(json);

            // ? Update DB
            tokenData.AccessToken = obj.access_token;
            tokenData.RefreshToken = obj.refresh_token ?? tokenData.RefreshToken; // ?? sometimes new milta hai
            tokenData.ExpiryTime = DateTime.UtcNow.AddSeconds((int)obj.expires_in);

            await _context.SaveChangesAsync();

            return tokenData;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"? Outlook Token Refresh Error: {ex.Message}");
            return null;
        }
    }
}










