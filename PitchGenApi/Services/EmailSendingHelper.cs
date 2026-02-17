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

    public async Task<bool> SendEmailUsingSmtp(
        int clientId,
        int contactId,
        int? dataFileId,
        int? SegmentId,
        int? CampaignId,
        string toEmail,
        string subject,
        bool isFollowUp,
        string BccEmail = "",
        int SmtpID = 0,
        string fullname = "",
        string location = "",
        string company = "",
        string website = "",
        string linkedin = "",
        string jobtitle = "")
    {
        var EmailDetails = await _context.contacts.FirstOrDefaultAsync(x => x.id == contactId);


        var smtpCredential = await _context.SmtpCredentials.FirstOrDefaultAsync(x => x.Id == SmtpID);

        var Blueprint = await _context.Campaigns.FirstOrDefaultAsync(x => x.Id == CampaignId);

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
                DataFileId = dataFileId,
                CampaignId = CampaignId,
                BlueprintId = Blueprint.TemplateId,
                SegmentId = SegmentId,
                ToEmail = toEmail,
                Subject = subject,
                Body = EmailDetails.email_body,
                IsSuccess = false,
                ErrorMessage = "SMTP credentials not found or invalid.",
                zohoViewName = "from pichkraft",
                SentAt = DateTime.UtcNow
            });
            await _context.SaveChangesAsync();
            return false;
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


            if (dataFileId == null)
            {
                var list = await _context.segments
                    .FirstOrDefaultAsync(s => s.Id == SegmentId && s.ClientId == clientId);

                


                if (isFollowUp)
                {
                    string oldThread = await _repository.BuildEmailThreadAsync(clientId, list.DataFileId, EmailDetails.id, SegmentId);

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

            // Send main email
            if (!string.IsNullOrWhiteSpace(toEmail))
            {
                string bodyWithTracking = EmailTrackingHelper.InjectClickTracking(toEmail, finalEmailBody, clientId,contactId, dataFileId, SegmentId, fullname, location, company, website, linkedin, jobtitle, trackingId,CampaignId,Blueprint.TemplateId);
                bodyWithTracking += EmailTrackingHelper.GetPixelTag(toEmail, clientId, dataFileId, SegmentId, contactId, fullname, location, company, website, linkedin, jobtitle, trackingId,CampaignId,Blueprint.TemplateId);

                using var toMessage = new MailMessage
                {
                    From = new MailAddress(fromEmailToUse,senderName),
                    Subject = subject,
                    Body = bodyWithTracking,
                    IsBodyHtml = true
                };

                toMessage.To.Add(toEmail);
                await smtpClient.SendMailAsync(toMessage);

                _context.EmailLogs.Add(new EmailLog
                {
                    ClientId = clientId,
                    ContactId = contactId,
                    CampaignId = CampaignId,
                    BlueprintId = Blueprint.TemplateId,
                    ToEmail = toEmail,
                    Subject = subject,
                    Body = EmailDetails.email_body,
                    EmailRecipientName = fullname,
                    EmailSenderName = senderName,
                    SenderEmailId = fromEmailToUse,
                    zohoViewName = "from pitch craft",
                    DataFileId = dataFileId,
                    SegmentId = SegmentId,
                    IsSuccess = true,
                    SentAt = DateTime.UtcNow,
                    TrackingId = Guid.Parse(trackingId),
                    process_name = "Single"
                });
            }

            // Send BCC email
            if (!string.IsNullOrWhiteSpace(BccEmail))
            {
                using var bccMessage = new MailMessage
                {
                    From = new MailAddress(fromEmailToUse),
                    Subject = subject,
                    Body = EmailDetails.email_body,
                    IsBodyHtml = true
                };

                // Add a visible recipient for compatibility
                bccMessage.Bcc.Add(BccEmail);

                await smtpClient.SendMailAsync(bccMessage);
            }

            await _context.SaveChangesAsync();
            return true;
        }
        catch (Exception ex)
        {
            _context.EmailLogs.Add(new EmailLog
            {
                ClientId = clientId,
                ContactId = contactId,
                CampaignId = CampaignId,
                BlueprintId = Blueprint.TemplateId,
                ToEmail = toEmail,
                Subject = subject,
                Body = EmailDetails.email_body,
                EmailRecipientName = fullname,
                EmailSenderName = senderName,
                SenderEmailId = fromEmailToUse,
                IsSuccess = false,
                ErrorMessage = ex.Message,
                zohoViewName = "from pitch craft",
                DataFileId= dataFileId,
                SegmentId = SegmentId,
                SentAt = DateTime.UtcNow,
                process_name = "Single"
            });

            await _context.SaveChangesAsync();
            return false;
        }
    }
}
