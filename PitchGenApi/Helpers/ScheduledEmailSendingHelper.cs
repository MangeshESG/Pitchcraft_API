using Microsoft.EntityFrameworkCore;
using PitchGenApi.Database;
using PitchGenApi.Services;
using System.Net.Mail;
using System.Net;
using PitchGenApi.Models;
using PitchGenApi.Model;
using PitchGenApi.Interfaces;

public class ScheduledEmailSendingHelper
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ContactRepository _contactRepository;
    private readonly IDomainVerificationRepository _domain;

    public ScheduledEmailSendingHelper(IServiceProvider serviceProvider, ContactRepository contactRepository,IDomainVerificationRepository domain)
    {
        _serviceProvider = serviceProvider;
        _contactRepository = contactRepository;
        _domain = domain;
    }

    public async Task ProcessStepAsync(SequenceStep step, CancellationToken cancellationToken)
    {
        Console.WriteLine($"📧 Starting ProcessStepAsync for Step ID: {step?.Id}");

        using var scope = _serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        if (step == null || step.TimeZone == null)
        {
            Console.WriteLine("⚠️ Step or TimeZone is null — skipping.");
            return; // ADD THIS RETURN
        }

        if ((!step.DataFileId.HasValue || step.DataFileId.Value <= 0) &&
            (!step.SegmentId.HasValue || step.SegmentId.Value <= 0))
        {
            Console.WriteLine("⚠️ Both DataFileId and SegmentId are invalid — skipping.");
            return;
        }

        if ((!step.DataFileId.HasValue || step.DataFileId.Value <= 0) &&
            (!step.SegmentId.HasValue || step.SegmentId.Value <= 0))
        {
            Console.WriteLine("⚠️ Both DataFileId and SegmentId are invalid — skipping.");
            return;
        }
        if (step == null || step.TimeZone == null || step.SegmentId == null)
        {
            Console.WriteLine("⚠️ Step, TimeZone or Segmentid is null — skipping.");
        }
        var scheduledUtc = step.ScheduledDate + step.ScheduledTime;
        if (scheduledUtc > DateTime.UtcNow || step.SmtpID == 0)
        {
            Console.WriteLine($"⏳ Step not due yet (scheduled: {scheduledUtc:yyyy-MM-dd HH:mm:ss} UTC, now: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC) or invalid SMTP ID — skipping.");
            return;
        }

        var smtpCredential = await context.SmtpCredentials
            .FirstOrDefaultAsync(x => x.Id == step.SmtpID, cancellationToken);

        if (smtpCredential == null)
        {
            Console.WriteLine($"❌ SMTP credentials not found for ID: {step.SmtpID}");
            return;
        }

        List<Contact> contacts;

        if (step.DataFileId.HasValue && step.DataFileId.Value > 0)
        {
            Console.WriteLine($"📂 Fetching contacts for DataFileId: {step.DataFileId}");
            contacts = await _contactRepository.GetContactsAsync(step.DataFileId.Value); // Use .Value
        }
        else if (step.SegmentId.HasValue && step.SegmentId.Value > 0)
        {
            Console.WriteLine($"📂 Fetching contacts for SegmentId: {step.SegmentId}");
            contacts = await _contactRepository.GetContactBySegment(step.SegmentId.Value); // Use .Value
        }
        else
        {
            Console.WriteLine("⚠️ Both DataFileId and SegmentId are invalid — skipping contacts fetch.");
            return;
        }

        Console.WriteLine($"👥 Total contacts fetched: {contacts.Count}");

        var user = await context.ClientDetails.FirstOrDefaultAsync(x => x.Id == step.ClientId);


        var sentEmails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        bool isVerified = await _domain.IsSmtpFullyVerifiedAsync(step.SmtpID);

        string smtpServer = smtpCredential.Server;
        int smtpPort = smtpCredential.Port;
        bool useSsl = smtpCredential.UseSsl;
        string smtpUsername = smtpCredential.Username;
        string smtpPassword = smtpCredential.Password;

        string fromEmailToUse = smtpCredential.FromEmail;
        string senderName = smtpCredential.SenderName;

        foreach (var Contact in contacts)
        {
            if (!isVerified)
            {
                // ❌ NOT VERIFIED → FALLBACK SMTP
                smtpServer = "mail.sender.pitchkraft.ai";
                smtpPort = 587;
                useSsl = true;
                smtpUsername = "message-service@sender.pitchkraft.ai";
                smtpPassword = "yV%691jd9";

                fromEmailToUse = "message-service@sender.pitchkraft.ai";
                senderName = "PitchCraft";
            }

            if (Contact == null || string.IsNullOrWhiteSpace(Contact.email))
                continue;

            bool isUnsubscribed = await context.UnsubscribedContacts
                .AnyAsync(x => x.ClientId == step.ClientId &&
                               x.Email.ToLower() == Contact.email.ToLower(),
                          cancellationToken);

            // ❗ Email subject/body validation (DO NOT STOP BULK)
            if (string.IsNullOrWhiteSpace(Contact.email_subject) ||
                Contact.email_subject.Trim().ToUpper() == "N/A" ||
                string.IsNullOrWhiteSpace(Contact.email_body) ||
                Contact.email_body.Trim().ToUpper() == "N/A")
            {
                context.EmailLogs.Add(new EmailLog
                {
                    StepId = step.Id,
                    ToEmail = Contact.email,
                    ContactId = Contact.id,
                    Subject = Contact.email_subject,
                    Body = Contact.email_body,
                    IsSuccess = false,
                    ErrorMessage = "Email body or subject is incorrect.",
                    zohoViewName = "from pitch craft",
                    EmailRecipientName = Contact.full_name,
                    EmailSenderName = senderName,
                    SenderEmailId = fromEmailToUse,
                    DataFileId = step.DataFileId,
                    SegmentId = step.SegmentId,
                    SentAt = DateTime.UtcNow,
                    ClientId = step.ClientId,
                    TrackingId = Guid.NewGuid(),
                    process_name = "Bulk"
                });

                continue; // ✅ important (NO return)
            }

            if (isUnsubscribed)
            {
                Console.WriteLine($"🚫 Skipping email to {Contact.email} - User Unsubscribed.");

                context.EmailLogs.Add(new EmailLog
                {
                    StepId = step.Id,
                    ToEmail = Contact.email,
                    ContactId = Contact.id,
                    Subject = Contact.email_subject,
                    Body = Contact.email_body,
                    EmailRecipientName = Contact.full_name,
                    EmailSenderName = senderName,
                    SenderEmailId = fromEmailToUse,
                    IsSuccess = false,
                    ErrorMessage = "Unsubscribed",
                    zohoViewName = "from pitch craft",
                    DataFileId = step.DataFileId,
                    SegmentId = step.SegmentId,
                    SentAt = DateTime.UtcNow,
                    ClientId = step.ClientId,
                    TrackingId = Guid.NewGuid(),
                    process_name = "Bulk"
                });

                continue;
            }

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

            string finalEmailBody = Contact.email_body;

            if (step.IsFollowUp == true)
            {
                string oldThread = await _contactRepository
                    .BuildEmailThreadAsync(step.ClientId, step.DataFileId, Contact.id, step.SegmentId);

                finalEmailBody =
                $@"{Contact.email_body}

                {oldThread}";
            }

            sentEmails.Add(Contact.email);

            string subject = Contact.email_subject;
            string toEmail = Contact.email;

            // ✅ Use finalEmailBody (fix)
            string bodyWithTracking = finalEmailBody;

            bodyWithTracking = EmailTrackingHelper.InjectClickTracking(
                Contact.email,
                bodyWithTracking,
                step.ClientId,
                Contact.id,
                step.DataFileId ?? 0,
                step.SegmentId ?? 0,
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
                step.SegmentId ?? 0,
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
                using var smtpClient = new SmtpClient(smtpServer)
                {
                    Port = smtpPort,
                    Credentials = new NetworkCredential(smtpUsername, smtpPassword),
                    EnableSsl = useSsl,
                };

                using (var toMessage = new MailMessage
                {
                    From = new MailAddress(fromEmailToUse, senderName),
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
                    using var bccMessage = new MailMessage
                    {
                        From = new MailAddress(fromEmailToUse, senderName),
                        Subject = subject,
                        Body = Contact.email_body,
                        IsBodyHtml = true,
                        BodyEncoding = System.Text.Encoding.UTF8,
                        SubjectEncoding = System.Text.Encoding.UTF8,
                    };

                    bccMessage.To.Add(new MailAddress("pitch.craft@virtual-employees.co.uk", Contact.email));
                    bccMessage.Bcc.Add(step.BccEmail);

                    await smtpClient.SendMailAsync(bccMessage, cancellationToken);

                    var nowUtc = DateTime.UtcNow;

                    var dbContact = await context.contacts
                        .AsTracking()
                        .FirstOrDefaultAsync(c => c.email == toEmail &&
                                                  c.DataFileId == step.DataFileId,
                                              cancellationToken);

                    if (dbContact != null)
                    {
                        dbContact.email_sent_at = nowUtc;
                        context.Entry(dbContact).Property(x => x.email_sent_at).IsModified = true;
                        context.Entry(dbContact).Property(x => x.updated_at).IsModified = true;
                        await context.SaveChangesAsync(cancellationToken);
                    }
                }

                context.EmailLogs.Add(new EmailLog
                {
                    StepId = step.Id,
                    ToEmail = toEmail,
                    ContactId = Contact.id,
                    Subject = subject,
                    Body = Contact.email_body,
                    IsSuccess = true,
                    zohoViewName = "from pitch craft",
                    EmailRecipientName = Contact.full_name,
                    EmailSenderName = senderName,
                    SenderEmailId = fromEmailToUse,
                    DataFileId = step.DataFileId,
                    SegmentId = step.SegmentId,
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
                    ContactId = Contact.id,
                    Subject = subject,
                    Body = Contact.email_body,
                    IsSuccess = false,
                    ErrorMessage = ex.Message,
                    EmailRecipientName = Contact.full_name,
                    EmailSenderName = senderName,
                    SenderEmailId = fromEmailToUse,
                    zohoViewName = "from pitch craft",
                    DataFileId = step.DataFileId,
                    SegmentId = step.SegmentId,
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
