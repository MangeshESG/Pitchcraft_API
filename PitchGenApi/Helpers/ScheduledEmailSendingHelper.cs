using Microsoft.EntityFrameworkCore;
using PitchGenApi.Database;
using PitchGenApi.Interfaces;
using PitchGenApi.Model;
using PitchGenApi.Models;
using PitchGenApi.Services;

public class ScheduledEmailSendingHelper
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ContactRepository _contactRepository;
    private readonly IDomainVerificationRepository _domain;

    public ScheduledEmailSendingHelper(IServiceProvider serviceProvider, ContactRepository contactRepository, IDomainVerificationRepository domain)
    {
        _serviceProvider = serviceProvider;
        _contactRepository = contactRepository;
        _domain = domain;
    }

    public async Task ProcessStepAsync(SequenceStep step, CancellationToken cancellationToken)
    {
        Console.WriteLine($"[ScheduledEmail] START StepId={step?.Id}, TriggerUtc={DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");

        using var scope = _serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var emailHelper = scope.ServiceProvider.GetRequiredService<EmailSendingHelper>();

        if (step == null || step.TimeZone == null)
        {
            Console.WriteLine("[ScheduledEmail] SKIP: Step or TimeZone is null.");
            return;
        }

        if ((!step.DataFileId.HasValue || step.DataFileId.Value <= 0) &&
            (!step.SegmentId.HasValue || step.SegmentId.Value <= 0))
        {
            Console.WriteLine($"[ScheduledEmail] SKIP StepId={step.Id}: Both DataFileId and SegmentId are invalid.");
            return;
        }

        var scheduledUtc = step.ScheduledDate + step.ScheduledTime;
        if (scheduledUtc > DateTime.UtcNow || step.SmtpID == 0)
        {
            Console.WriteLine($"[ScheduledEmail] SKIP StepId={step.Id}: Not due or invalid outbox. ScheduledUtc={scheduledUtc:yyyy-MM-dd HH:mm:ss}, NowUtc={DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}, OutboxId={step.SmtpID}");
            return;
        }

        List<Contact> contacts;

        if (step.DataFileId.HasValue && step.DataFileId.Value > 0)
        {
            Console.WriteLine($"[ScheduledEmail] StepId={step.Id}: Fetching contacts by DataFileId={step.DataFileId}");
            contacts = await _contactRepository.GetContactsAsync(step.DataFileId.Value);
        }
        else if (step.SegmentId.HasValue && step.SegmentId.Value > 0)
        {
            Console.WriteLine($"[ScheduledEmail] StepId={step.Id}: Fetching contacts by SegmentId={step.SegmentId}");
            contacts = await _contactRepository.GetContactBySegment(step.SegmentId.Value);
        }
        else
        {
            Console.WriteLine($"[ScheduledEmail] SKIP StepId={step.Id}: No valid contact source found.");
            return;
        }

        Console.WriteLine($"[ScheduledEmail] StepId={step.Id}: Total contacts fetched={contacts.Count}");

        var sentEmails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int successCount = 0;
        int failCount = 0;
        int skipCount = 0;
        string provider = NormalizeProvider(step.Provider);

        Console.WriteLine($"[ScheduledEmail] StepId={step.Id}: Provider={provider}, OutboxId={step.SmtpID}, CampaignId={step.CampaignId}, IsFollowUp={step.IsFollowUp == true}, Bcc={(string.IsNullOrWhiteSpace(step.BccEmail) ? "No" : "Yes")}");

        foreach (var contact in contacts)
        {
            if (contact == null || string.IsNullOrWhiteSpace(contact.email))
            {
                skipCount++;
                Console.WriteLine($"[ScheduledEmail] StepId={step.Id}: SKIP contact because email is empty/null.");
                continue;
            }

            bool isUnsubscribed = await context.UnsubscribedContacts
                .AnyAsync(x => x.ClientId == step.ClientId &&
                               x.Email.ToLower() == contact.email.ToLower(),
                          cancellationToken);

            if (isUnsubscribed)
            {
                skipCount++;
                Console.WriteLine($"[ScheduledEmail] StepId={step.Id}, ContactId={contact.id}, Email={contact.email}: SKIP unsubscribed.");
                continue;
            }

            if (sentEmails.Contains(contact.email))
            {
                skipCount++;
                Console.WriteLine($"[ScheduledEmail] StepId={step.Id}, ContactId={contact.id}, Email={contact.email}: SKIP duplicate email in this batch.");
                continue;
            }

            bool alreadySent = await context.EmailLogs
                .AnyAsync(x => x.StepId == step.Id && x.ToEmail == contact.email, cancellationToken);

            if (alreadySent)
            {
                skipCount++;
                Console.WriteLine($"[ScheduledEmail] StepId={step.Id}, ContactId={contact.id}, Email={contact.email}: SKIP already sent for this step.");
                continue;
            }

            sentEmails.Add(contact.email);
            Console.WriteLine($"[ScheduledEmail] StepId={step.Id}, ContactId={contact.id}, Email={contact.email}: Sending via {provider}...");

            var result = provider switch
            {
                "Gmail" => await emailHelper.SendEmailUsingGmailApi(
                    step.ClientId,
                    contact.id,
                    step.CampaignId,
                    step.IsFollowUp == true,
                    step.BccEmail ?? string.Empty,
                    step.SmtpID),

                "Outlook" => await emailHelper.SendEmailUsingOutlookApi(
                    step.ClientId,
                    contact.id,
                    step.CampaignId,
                    step.IsFollowUp == true,
                    step.BccEmail ?? string.Empty,
                    step.SmtpID),

                _ => await emailHelper.SendEmailUsingSmtp(
                    step.ClientId,
                    contact.id,
                    step.CampaignId,
                    step.IsFollowUp == true,
                    step.BccEmail ?? string.Empty,
                    step.SmtpID)
            };

            bool logUpdated = await MarkLatestLogAsScheduledAsync(context, step, contact, provider, cancellationToken);
            Console.WriteLine($"[ScheduledEmail] StepId={step.Id}, ContactId={contact.id}, Email={contact.email}: Latest log update={(logUpdated ? "OK" : "NOT_FOUND")}");

            if (result.Success)
            {
                successCount++;
                Console.WriteLine($"[ScheduledEmail] StepId={step.Id}, ContactId={contact.id}, Email={contact.email}: SUCCESS via {provider}. Message={result.Message}");
            }
            else
            {
                failCount++;
                Console.WriteLine($"[ScheduledEmail] StepId={step.Id}, ContactId={contact.id}, Email={contact.email}: FAILED via {provider}. Message={result.Message}");
            }
        }

        var dbStep = await context.SequenceSteps.FirstOrDefaultAsync(x => x.Id == step.Id, cancellationToken);
        if (dbStep != null)
        {
            dbStep.TestIsSent = true;
            Console.WriteLine($"[ScheduledEmail] StepId={step.Id}: Marked as sent. Success={successCount}, Failed={failCount}, Skipped={skipCount}");
        }
        else
        {
            Console.WriteLine($"[ScheduledEmail] StepId={step.Id}: Step not found while marking sent.");
        }

        await context.SaveChangesAsync(cancellationToken);
        Console.WriteLine($"[ScheduledEmail] END StepId={step.Id}: Changes saved. Success={successCount}, Failed={failCount}, Skipped={skipCount}, CompletedUtc={DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
    }

    private static string NormalizeProvider(string? provider)
    {
        return provider?.Trim().ToUpper() switch
        {
            "GMAIL" => "Gmail",
            "OUTLOOK" => "Outlook",
            _ => "SMTP"
        };
    }

    private static async Task<bool> MarkLatestLogAsScheduledAsync(
        AppDbContext context,
        SequenceStep step,
        Contact contact,
        string provider,
        CancellationToken cancellationToken)
    {
        var latestLog = await context.EmailLogs
            .Where(x => x.ClientId == step.ClientId &&
                        x.ContactId == contact.id &&
                        x.CampaignId == step.CampaignId &&
                        x.ToEmail == contact.email &&
                        x.outboxid == step.SmtpID)
            .OrderByDescending(x => x.SentAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (latestLog == null)
            return false;

        latestLog.StepId = step.Id;
        latestLog.Provider = provider;
        latestLog.outboxid = step.SmtpID;
        latestLog.process_name = "Bulk";
        return true;
    }
}
