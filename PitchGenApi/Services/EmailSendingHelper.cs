using System;
using System.Net;
using System.Net.Mail;
using System.Threading.Tasks;
using PitchGenApi.Database;
using Microsoft.EntityFrameworkCore;
using PitchGenApi.Model;

public class EmailSendingHelper
{
    private readonly AppDbContext _context;
    private readonly ContactRepository _repository;

    public EmailSendingHelper(AppDbContext context, ContactRepository repository)
    {
        _context = context;
        _repository = repository;
    }

    public async Task<bool> SendEmailUsingSmtp(
        int clientId,
        int contactId,
        int? dataFileId,
        int? SegmentId,
        string toEmail,
        string subject,
        bool isFollowUp,
        string BccEmail = "",
        int SmtpID = 0,
        string fullName = "",
        string location = "",
        string company = "",
        string website = "",
        string linkedin = "",
        string jobtitle = "")
    {
        var EmailDetails = await _context.contacts.FirstOrDefaultAsync(x => x.id == contactId);


        var smtpCredential = await _context.SmtpCredentials.FirstOrDefaultAsync(x => x.Id == SmtpID);

        if (smtpCredential == null || string.IsNullOrEmpty(smtpCredential.Server))
        {
            _context.EmailLogs.Add(new EmailLog
            {
                ClientId = clientId,
                ContactId = contactId,
                DataFileId = dataFileId,
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

        try
        {
            string trackingId = Guid.NewGuid().ToString();

            using var smtpClient = new SmtpClient(smtpCredential.Server)
            {
                Port = smtpCredential.Port,
                Credentials = new NetworkCredential(smtpCredential.Username, smtpCredential.Password),
                EnableSsl = smtpCredential.UseSsl,
            };

            string finalEmailBody = EmailDetails.email_body;


            if (dataFileId == null)
            {
                var list = await _context.segments
                    .FirstOrDefaultAsync(s => s.Id == SegmentId && s.ClientId == clientId);

                


                if (isFollowUp)
                {
                    string oldThread = await _repository.BuildEmailThreadAsync(clientId, list.DataFileId, EmailDetails.id, SegmentId);

                    finalEmailBody =
                    $@"{EmailDetails.email_body}

                {oldThread}";
                }
            }


            if (isFollowUp)
            {
                string oldThread = await _repository.BuildEmailThreadAsync(clientId, dataFileId, EmailDetails.id, SegmentId);

                finalEmailBody =
                $@"{EmailDetails.email_body}

                {oldThread}";
            }

            // Send main email
            if (!string.IsNullOrWhiteSpace(toEmail))
            {
                string bodyWithTracking = EmailTrackingHelper.InjectClickTracking(toEmail, finalEmailBody, clientId,contactId, dataFileId, SegmentId, fullName, location, company, website, linkedin, jobtitle, trackingId);
                bodyWithTracking += EmailTrackingHelper.GetPixelTag(toEmail, clientId, dataFileId, SegmentId, contactId, fullName, location, company, website, linkedin, jobtitle, trackingId);

                using var toMessage = new MailMessage
                {
                    From = new MailAddress(smtpCredential.FromEmail),
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
                    ToEmail = toEmail,
                    Subject = subject,
                    Body = EmailDetails.email_body,
                    EmailRecipientName = fullName,
                    EmailSenderName = $"{user.FirstName} {user.LastName}",
                    SenderEmailId = smtpCredential.FromEmail,
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
                    From = new MailAddress(smtpCredential.FromEmail),
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
                ToEmail = toEmail,
                Subject = subject,
                Body = EmailDetails.email_body,
                EmailRecipientName = fullName,
                EmailSenderName = $"{user.FirstName} {user.LastName}",
                SenderEmailId = smtpCredential.FromEmail,
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
