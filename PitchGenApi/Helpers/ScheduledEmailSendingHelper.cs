using Microsoft.EntityFrameworkCore;
using PitchGenApi.Database;
using PitchGenApi.Services;
using System.Net.Mail;
using System.Net;

public class ScheduledEmailSendingHelper
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ZohoService _zohoService;

    public ScheduledEmailSendingHelper(IServiceProvider serviceProvider, ZohoService zohoService)
    {
        _serviceProvider = serviceProvider;
        _zohoService = zohoService;
    }

    public async Task ProcessStepAsync(SequenceStep step, CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        if (step == null || step.TimeZone == null || step.zohoviewName == null)
            return;

        var scheduledUtc = step.ScheduledDate + step.ScheduledTime;
        if (scheduledUtc > DateTime.UtcNow || step.SmtpID == 0)
            return;

        var smtpCredential = await context.SmtpCredentials
            .FirstOrDefaultAsync(x => x.Id == step.SmtpID, cancellationToken);

        if (smtpCredential == null)
            return;

        string pageToken = null;
        bool moreRecords = true;

        while (moreRecords && !cancellationToken.IsCancellationRequested)
        {
            var result = await _zohoService.GetFilteredZohoDataAsync(step.zohoviewName, pageToken);
            var contacts = result.FilteredData;
            var sentEmails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string trackingId = Guid.NewGuid().ToString();


            foreach (var contact in contacts)
            {
                if (contact == null || string.IsNullOrWhiteSpace(contact.Email))
                    continue;

                if (sentEmails.Contains(contact.Email))
                    continue;

                bool alreadySent = await context.EmailLogs
                    .AnyAsync(x => x.StepId == step.Id && x.ToEmail == contact.Email, cancellationToken);

                if (alreadySent)
                    continue;

                sentEmails.Add(contact.Email);

                string subject = contact.Subject ?? "No Subject";
                string toEmail = contact.Email;

                string bodyWithTracking = (contact.Body ?? "No Content");
                bodyWithTracking = EmailTrackingHelper.InjectClickTracking(contact.Email, bodyWithTracking, step.ClientId, step.zohoviewName, contact.FullName, contact.Location, contact.Company, contact.website, contact.linkedin_URL, contact.JobTitle, trackingId);
                bodyWithTracking += EmailTrackingHelper.GetPixelTag(contact.Email, step.ClientId, step.zohoviewName, contact.FullName, contact.Location, contact.Company, contact.website, contact.linkedin_URL, contact.JobTitle, trackingId);

                try
                {

                    using var smtpClient = new SmtpClient(smtpCredential.Server)
                    {
                        Port = smtpCredential.Port,
                        Credentials = new NetworkCredential(smtpCredential.Username, smtpCredential.Password),
                        EnableSsl = true,
                    };

                    using (var toMessage = new MailMessage
                    {
                        From = new MailAddress(smtpCredential.FromEmail),
                        Subject = subject,
                        Body = bodyWithTracking,
                        IsBodyHtml = true,
                        BodyEncoding = System.Text.Encoding.UTF8,
                        SubjectEncoding = System.Text.Encoding.UTF8,
                    })
                    {
                        toMessage.To.Add(toEmail);
                        await smtpClient.SendMailAsync(toMessage, cancellationToken);
                    }

                    if (!string.IsNullOrWhiteSpace(step.BccEmail))
                    {
                        string cleanBody = contact.Body ?? "No Content";

                        using var bccMessage = new MailMessage
                        {
                            From = new MailAddress(smtpCredential.FromEmail),
                            Subject = subject,
                            Body = cleanBody,
                            IsBodyHtml = true,
                            BodyEncoding = System.Text.Encoding.UTF8,
                            SubjectEncoding = System.Text.Encoding.UTF8,
                        };

                        // ✅ "To" field me actual recipient ka naam & email dikhe BCC mail me
                        bccMessage.To.Add(new MailAddress("pitch.craft@virtual-employees.co.uk", contact.Email)); // 👈 shows only email ID in To

                        // ✅ Real recipient of this email is BccEmail
                        bccMessage.Bcc.Add(step.BccEmail);

                        await smtpClient.SendMailAsync(bccMessage, cancellationToken);
                    }


                    context.EmailLogs.Add(new EmailLog
                    {
                        StepId = step.Id,
                        ToEmail = toEmail,
                        Subject = subject,
                        Body = bodyWithTracking,
                        IsSuccess = true,
                        ErrorMessage = null,
                        zohoViewName = step.zohoviewName,
                        SentAt = DateTime.UtcNow,
                        ClientId = step.ClientId,
                        TrackingId = Guid.Parse(trackingId),
                        process_name = "Bulk"
                    });
                }
                catch (Exception ex)
                {
                    context.EmailLogs.Add(new EmailLog
                    {
                        StepId = step.Id,
                        ToEmail = toEmail,
                        Subject = subject,
                        Body = bodyWithTracking,
                        IsSuccess = false,
                        ErrorMessage = ex.Message,
                        zohoViewName = step.zohoviewName,
                        SentAt = DateTime.UtcNow,
                        ClientId = step.ClientId,
                        TrackingId = Guid.Parse(trackingId),
                        process_name = "Bulk"

                    });
                }
            }

            pageToken = result.NextPageToken;
            moreRecords = result.MoreRecords ?? false;
        }

        var dbStep = await context.SequenceSteps.FirstOrDefaultAsync(x => x.Id == step.Id, cancellationToken);
        if (dbStep != null)
        {
            dbStep.IsSent = true;
        }

        await context.SaveChangesAsync(cancellationToken);
    }
}
