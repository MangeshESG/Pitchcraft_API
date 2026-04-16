using System;
using System.Net;
using System.Net.Mail;
using System.Threading.Tasks;
using PitchGenApi.Database;
using Microsoft.EntityFrameworkCore;
using PitchGenApi.Model;
using System.Text.RegularExpressions;
using PitchGenApi.Interfaces;
using Org.BouncyCastle.Crypto;
using PitchGenApi.Model.DTOs;
using static PitchGenApi.Model.ChatGptResponse;
using MimeKit;
using MimeKit.Utils;

public class EmailSendingHelper
{
    private readonly AppDbContext _context;
    private readonly ContactRepository _repository;
    private readonly IDomainVerificationRepository _domain;

    public EmailSendingHelper(AppDbContext context, ContactRepository repository,IDomainVerificationRepository domain)
    {
        _context = context;
        _repository = repository;
        _domain = domain;
    }

    public async Task<EmailSendResult> SendEmailUsingSmtp(int clientId, int contactId, int? CampaignId,bool isFollowUp, string BccEmail = "", int SmtpID = 0)
    {
        var EmailDetails = await _context.contacts.FirstOrDefaultAsync(x => x.id == contactId);


        var smtpCredential = await _context.SmtpCredentials.FirstOrDefaultAsync(x => x.Id == SmtpID);

        var Blueprint = await _context.Campaigns.FirstOrDefaultAsync(x => x.Id == CampaignId);
        int DataFileId = 0;

        if (!string.IsNullOrWhiteSpace(Blueprint.ZohoViewId))
        {
            int.TryParse(Blueprint.ZohoViewId, out DataFileId);
        }


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
                BlueprintId = Blueprint.TemplateId,
                SegmentId = Blueprint.SegmentId,
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

        var user = await _context.ClientDetails.FirstOrDefaultAsync(x => x.Id == clientId);

        //bool isVerified = await _domain.IsSmtpFullyVerifiedAsync(SmtpID);

        string smtpServer = smtpCredential.Server;
        int smtpPort = smtpCredential.Port;
        bool useSsl = smtpCredential.UseSsl;
        string smtpUsername = smtpCredential.Username;
        string smtpPassword = smtpCredential.Password;

        string fromEmailToUse = smtpCredential.FromEmail;
        string senderName = smtpCredential.SenderName;

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

            string messageId = MimeUtils.GenerateMessageId();

            using var smtpClient = new SmtpClient(smtpServer)
            {
                Port = smtpPort,
                Credentials = new NetworkCredential(smtpUsername, smtpPassword),
                EnableSsl = useSsl,
            };

            string finalEmailBody = EmailDetails.email_body;
            
            string emailFooter = @"<br/><br/>
                <hr style='border:none;border-top:1px solid #e5e7eb;'/>
                <p style='font-size:12px;color:#6b7280;text-align:center;'>
                This message was sent from 
                <a href='https://app.pitchkraft.ai/' target='_blank' style='color:#2563eb;text-decoration:none;font-weight:bold;'>
                Pitchkraft.ai
                </a>
                </p>";


            if (DataFileId == null)
            {
                var list = await _context.segments
                    .FirstOrDefaultAsync(s => s.Id == Blueprint.SegmentId && s.ClientId == clientId);

                


                if (isFollowUp)
                {
                    string oldThread = await _repository.BuildEmailThreadAsync(clientId, list.DataFileId, EmailDetails.id, Blueprint.SegmentId);

                    finalEmailBody =
                     $@"{EmailDetails.email_body}

                    {oldThread}
                    {emailFooter}";

                }
            }


            if (!active)
            {
                finalEmailBody += emailFooter;
            }
            // 🔥 STEP 1: Hidden tracking inject (reply ke liye)
            finalEmailBody = EmailTrackingHelper.InjectinboxTracking(finalEmailBody, trackingId);

            // Send main email
            if (!string.IsNullOrWhiteSpace(EmailDetails.email))
            {
                if (user.IsTracking)
                {
                    finalEmailBody = EmailTrackingHelper.InjectClickTracking(finalEmailBody, trackingId);
                    finalEmailBody += EmailTrackingHelper.GetPixelTag(trackingId);
                }

                using var toMessage = new MailMessage
                {
                    From = new MailAddress(fromEmailToUse,senderName),
                    Subject = EmailDetails.email_subject,
                    Body = finalEmailBody,   //Body = finalEmailBody- for non traking     Body = bodyWithTracking- for traking
                    IsBodyHtml = true
                };
                toMessage.Headers.Add("Message-ID", messageId);

                toMessage.To.Add(EmailDetails.email);
                await smtpClient.SendMailAsync(toMessage);

                _context.EmailLogs.Add(new EmailLog
                {
                    ClientId = clientId,
                    ContactId = contactId,
                    CampaignId = CampaignId,
                    BlueprintId = Blueprint.TemplateId,
                    ToEmail = EmailDetails.email,
                    Subject = EmailDetails.email_subject,
                    Body = EmailDetails.email_body,
                    EmailRecipientName = EmailDetails.full_name,
                    EmailSenderName = senderName,
                    SenderEmailId = fromEmailToUse,
                    zohoViewName = "from pitch craft",
                    DataFileId = DataFileId,
                    SegmentId = Blueprint.SegmentId,
                    IsSuccess = true,
                    SentAt = DateTime.UtcNow,
                    TrackingId = Guid.Parse(trackingId),
                    MessageId = messageId,
                    process_name = "Single"
                });
            }

            // Send BCC email
            if (!string.IsNullOrWhiteSpace(BccEmail))
            {
                using var bccMessage = new MailMessage
                {
                    From = new MailAddress(fromEmailToUse),
                    Subject = EmailDetails.email_subject,
                    Body = EmailDetails.email_body,
                    IsBodyHtml = true
                };

                // Add a visible recipient for compatibility
                bccMessage.Bcc.Add(BccEmail);

                await smtpClient.SendMailAsync(bccMessage);
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
                BlueprintId = Blueprint.TemplateId,
                ToEmail = EmailDetails.email,
                Subject = EmailDetails.email_subject,
                Body = EmailDetails.email_body,
                EmailRecipientName = EmailDetails.full_name,
                EmailSenderName = senderName,
                SenderEmailId = fromEmailToUse,
                IsSuccess = false,
                ErrorMessage = ex.Message,
                zohoViewName = "from pitch craft",
                DataFileId= DataFileId,
                SegmentId = Blueprint.SegmentId,
                SentAt = DateTime.UtcNow,
                process_name = "Single"
            });

            await _context.SaveChangesAsync();
            return new EmailSendResult
            {
                Success = false,
                Message = ex.Message,

            };
        }
    }
}
