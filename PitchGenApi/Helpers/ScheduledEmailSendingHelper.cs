using Microsoft.EntityFrameworkCore;
using PitchGenApi.Database;
using PitchGenApi.Services;
using System.Net.Mail;
using System.Net;

public class ScheduledEmailSendingHelper
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ContactRepository _contactRepository;

    public ScheduledEmailSendingHelper(IServiceProvider serviceProvider, ContactRepository contactRepository)
    {
        _serviceProvider = serviceProvider;
        _contactRepository = contactRepository;
    }

    public async Task ProcessStepAsync(SequenceStep step, CancellationToken cancellationToken)
    {
        Console.WriteLine($"📧 Starting ProcessStepAsync for Step ID: {step?.Id}");

        using var scope = _serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        if (step == null || step.TimeZone == null || step.DataFileId == null)
        {
            Console.WriteLine("⚠️ Step, TimeZone or DataFileId is null — skipping.");
            return;
        }

        var scheduledUtc = step.ScheduledDate + step.ScheduledTime;
        if (scheduledUtc > DateTime.UtcNow || step.SmtpID == 0)
        {
            Console.WriteLine("⏳ Step not due yet or invalid SMTP ID — skipping.");
            return;
        }

        var smtpCredential = await context.SmtpCredentials
            .FirstOrDefaultAsync(x => x.Id == step.SmtpID, cancellationToken);

        if (smtpCredential == null)
        {
            Console.WriteLine($"❌ SMTP credentials not found for ID: {step.SmtpID}");
            return;
        }

        Console.WriteLine($"📂 Fetching contacts for DataFileId: {step.DataFileId}");
        var contacts = await _contactRepository.GetContactsAsync(step.DataFileId);
        Console.WriteLine($"👥 Total contacts fetched: {contacts.Count}");

        var sentEmails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var Contact in contacts)
        {
            if (Contact == null || string.IsNullOrWhiteSpace(Contact.email))
                continue;

            if (sentEmails.Contains(Contact.email))
                continue;
            string trackingId = Guid.NewGuid().ToString();

            bool alreadySent = await context.EmailLogs
                .AnyAsync(x => x.StepId == step.Id && x.ToEmail == Contact.email, cancellationToken);

            if (alreadySent)
            {
                Console.WriteLine($"ℹ️ Already sent to: {Contact.email} — skipping.");
                continue;
            }

            sentEmails.Add(Contact.email);
            string subject = Contact.email_subject ?? "No Subject";
            string toEmail = Contact.email;
            string bodyWithTracking = Contact.email_body ?? "No Content";

            bodyWithTracking = EmailTrackingHelper.InjectClickTracking(
                Contact.email,
                bodyWithTracking,
                step.ClientId,
                Contact.id,
                step.DataFileId ?? 0,
                Contact.full_name,
                Contact.country_or_address,
                Contact.company_name,
                Contact.website,
                Contact.linkedin_url,
                Contact.job_title,
                trackingId
            );

            bodyWithTracking += EmailTrackingHelper.GetPixelTag(
                Contact.email,
                step.ClientId,
                step.DataFileId ?? 0,
                Contact.id,
                Contact.full_name,
                Contact.country_or_address,
                Contact.company_name,
                Contact.website,
                Contact.linkedin_url,
                Contact.job_title,
                trackingId
            );

            try
            {
                
                using var smtpClient = new SmtpClient(smtpCredential.Server)
                {
                    Port = smtpCredential.Port,
                    Credentials = new NetworkCredential(smtpCredential.Username, smtpCredential.Password),
                    EnableSsl = smtpCredential.UseSsl,
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

                Console.WriteLine($"✅ Email sent to: {toEmail}");

                if (!string.IsNullOrWhiteSpace(step.BccEmail))
                {
                    string cleanBody = Contact.email_body ?? "No Content";

                    using var bccMessage = new MailMessage
                    {
                            From = new MailAddress(smtpCredential.FromEmail),
                        Subject = subject,
                        Body = cleanBody,
                        IsBodyHtml = true,
                        BodyEncoding = System.Text.Encoding.UTF8,
                        SubjectEncoding = System.Text.Encoding.UTF8,
                    };

                    bccMessage.To.Add(new MailAddress("pitch.craft@virtual-employees.co.uk", Contact.email));
                    bccMessage.Bcc.Add(step.BccEmail);

                    await smtpClient.SendMailAsync(bccMessage, cancellationToken);

                    Console.WriteLine($"📩 BCC sent for: {toEmail}");
                    var nowUtc = DateTime.UtcNow;

                    var dbContact = await context.contacts
                        .AsTracking()
                        .FirstOrDefaultAsync(c => c.email == toEmail && c.DataFileId == step.DataFileId, cancellationToken);

                    if (dbContact != null)
                    {
                        dbContact.email_sent_at = nowUtc;

                        context.Entry(dbContact).Property(x => x.email_sent_at).IsModified = true;
                        context.Entry(dbContact).Property(x => x.updated_at).IsModified = true;

                        var rows = await context.SaveChangesAsync(cancellationToken);
                        Console.WriteLine($"📌 Contacts updated: {rows}");
                    }
                    else
                    {
                        Console.WriteLine("⚠️ Contact not found for update (email/DataFileId mismatch).");
                    }

                }

                context.EmailLogs.Add(new EmailLog
                {
                    StepId = step.Id,
                    ToEmail = toEmail,
                    ContactId = Contact.id,
                    Subject = subject,
                    Body = bodyWithTracking,
                    IsSuccess = true,
                    ErrorMessage = null,
                    zohoViewName = "from pitch craft",
                    DataFileId = step.DataFileId,
                    SentAt = DateTime.UtcNow,
                    ClientId = step.ClientId,
                    TrackingId = Guid.Parse(trackingId),
                    process_name = "Bulk"
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Failed to send to: {toEmail} | Error: {ex.Message}");

                context.EmailLogs.Add(new EmailLog
                {
                    StepId = step.Id,
                    ToEmail = toEmail,
                    ContactId = Contact.id,
                    Subject = subject,
                    Body = bodyWithTracking,
                    IsSuccess = false,
                    ErrorMessage = ex.Message,
                    zohoViewName = "from pitch craft",
                    DataFileId = step.DataFileId,
                    SentAt = DateTime.UtcNow,
                    ClientId = step.ClientId,
                    TrackingId = Guid.Parse(trackingId),
                    process_name = "Bulk"
                });
            }
        }

        var dbStep = await context.SequenceSteps.FirstOrDefaultAsync(x => x.Id == step.Id, cancellationToken);
        if (dbStep != null)
        {
            dbStep.IsSent = true;
            Console.WriteLine($"🟢 Marked step ID {step.Id} as sent.");
        }

        await context.SaveChangesAsync(cancellationToken);
        Console.WriteLine("💾 Changes saved to database.");
    }
}
