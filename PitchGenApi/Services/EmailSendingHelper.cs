using System;
using System.Net;
using System.Net.Mail;
using System.Threading.Tasks;
using PitchGenApi.Database;
using Microsoft.EntityFrameworkCore;

public class EmailSendingHelper
{
    private readonly AppDbContext _context;

    public EmailSendingHelper(AppDbContext context)
    {
        _context = context;
    }

    public async Task<bool> SendEmailUsingSmtp(
        int clientId,
        int contactId,
        int dataFileId,
        string toEmail,
        string subject,
        string body,
        string BccEmail = "",
        int SmtpID = 0,
        string fullName = "",
        string location = "",
        string company = "",
        string website = "",
        string linkedin = "",
        string jobtitle = "")
    {
        var smtpCredential = await _context.SmtpCredentials.FirstOrDefaultAsync(x => x.Id == SmtpID);
        if (smtpCredential == null || string.IsNullOrEmpty(smtpCredential.Server))
        {
            _context.EmailLogs.Add(new EmailLog
            {
                ClientId = clientId,
                ContactId = contactId,
                ToEmail = toEmail,
                Subject = subject,
                Body = body,
                IsSuccess = false,
                ErrorMessage = "SMTP credentials not found or invalid.",
                zohoViewName = "from pichkraft",
                SentAt = DateTime.UtcNow
            });
            await _context.SaveChangesAsync();
            return false;
        }

        try
        {
            string trackingId = Guid.NewGuid().ToString();

            using var smtpClient = new SmtpClient(smtpCredential.Server)
            {
                Port = smtpCredential.Port,
                Credentials = new NetworkCredential(smtpCredential.Username, smtpCredential.Password),
                EnableSsl = smtpCredential.UseSsl,
            };

            // Send main email
            if (!string.IsNullOrWhiteSpace(toEmail))
            {
                string bodyWithTracking = EmailTrackingHelper.InjectClickTracking(toEmail, body, clientId,contactId, dataFileId, fullName, location, company, website, linkedin, jobtitle, trackingId);
                bodyWithTracking += EmailTrackingHelper.GetPixelTag(toEmail, clientId, dataFileId,contactId, fullName, location, company, website, linkedin, jobtitle, trackingId);

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
                    Body = bodyWithTracking,
                    zohoViewName = "from pitch craft",
                    DataFileId = dataFileId,
                    IsSuccess = true,
                    SentAt = DateTime.UtcNow,
                    TrackingId = Guid.Parse(trackingId),
                    process_name = "Singel"
                });
            }

            // Send BCC email
            if (!string.IsNullOrWhiteSpace(BccEmail))
            {
                using var bccMessage = new MailMessage
                {
                    From = new MailAddress(smtpCredential.FromEmail),
                    Subject = subject,
                    Body = body,
                    IsBodyHtml = true
                };

                // Add a visible recipient for compatibility
                bccMessage.To.Add(new MailAddress("pitch.craft@virtual-employees.co.uk", toEmail)); // 👈 shows only email ID in To
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
                Body = body,
                IsSuccess = false,
                ErrorMessage = ex.Message,
                zohoViewName = "from pitch craft",
                DataFileId= dataFileId,
                SentAt = DateTime.UtcNow,
                process_name = "Singel"
            });

            await _context.SaveChangesAsync();
            return false;
        }
    }
}
