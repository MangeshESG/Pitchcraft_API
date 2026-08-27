using DnsClient;
using Microsoft.EntityFrameworkCore;
using MailKit.Security;
using MailKit.Net.Imap;
using MailKit.Search;
using MimeKit;
using MimeKit.Utils;
using PitchGenApi.Database;
using PitchGenApi.Interfaces;
using PitchGenApi.Model.DTOs;
using PitchGenApi.Helpers;
using PitchGenApi.Models;
using System.Net.Mail;
using System.Net.Sockets;

namespace PitchGenApi.Repository
{
    public class ExtensionRepository : IExtensionRepository
    {
        private readonly AppDbContext _context;
        private readonly CalculateEmailRepository _calculateEmailRepository;
        private readonly ContactRepository _contactRepository;
        private readonly EmailTemplateHelper _emailTemplateHelper;
        private readonly IConfiguration _configuration;

        

        public ExtensionRepository(AppDbContext context, CalculateEmailRepository calculateEmailRepository, ContactRepository contactRepository, EmailTemplateHelper emailTemplateHelper, IConfiguration configuration)
        {
            _context = context;
            _calculateEmailRepository = calculateEmailRepository;
            _contactRepository = contactRepository;
            _emailTemplateHelper = emailTemplateHelper;
            _configuration = configuration;
        }

        public IReadOnlyList<string> GetAllEmailPatterns() => AllEmailPatterns;

        public async Task<ExtensionOperationResult> MatchContactAsync(ContactMatchRequestDto request)
        {
            static string Normalize(string? value) =>
                (value ?? string.Empty).Trim().ToLowerInvariant();

            static string NormalizeUrl(string? value) =>
                Normalize(value).TrimEnd('/');

            var normalizedLinkedInUrl = NormalizeUrl(request.LinkedInUrl);
            var clientContacts = await (
                from c in _context.contacts.AsNoTracking()
                join df in _context.data_files.AsNoTracking()
                    on c.DataFileId equals (int?)df.id
                where df.client_id == request.ClientId && c.linkedin_url != null
                select new
                {
                    c.id,
                    c.DataFileId,
                    c.full_name,
                    c.job_title,
                    c.linkedin_url,
                    c.company_name,
                    c.country_or_address,
                    df.name,
                    df.data_file_name
                }).ToListAsync();

            var contacts = clientContacts
                .Where(contact => NormalizeUrl(contact.linkedin_url) == normalizedLinkedInUrl)
                .ToList();

            if (contacts.Count == 0)
                return new ExtensionOperationResult(200, new { matched = false });

            var matchedContact = contacts.FirstOrDefault(contact =>
                Normalize(contact.full_name) == Normalize(request.ContactName) &&
                Normalize(contact.job_title) == Normalize(request.JobTitle) &&
                Normalize(contact.company_name) == Normalize(request.CompanyName) &&
                Normalize(contact.country_or_address) == Normalize(request.Location));

            if (matchedContact != null)
            {
                return new ExtensionOperationResult(200, new
                {
                    matched = true,
                    contactId = matchedContact.id,
                    dataFileId = matchedContact.DataFileId,
                    dataFileName = !string.IsNullOrWhiteSpace(matchedContact.name)
                        ? matchedContact.name
                        : matchedContact.data_file_name,
                    existingData = new
                    {
                        contactName = matchedContact.full_name,
                        jobTitle = matchedContact.job_title,
                        companyName = matchedContact.company_name,
                        location = matchedContact.country_or_address
                    }
                });
            }

            var existingContact = contacts[0];
            var unmatchedFields = new Dictionary<string, object?>();

            if (Normalize(existingContact.full_name) != Normalize(request.ContactName))
                unmatchedFields["contactName"] = request.ContactName;
            if (Normalize(existingContact.job_title) != Normalize(request.JobTitle))
                unmatchedFields["jobTitle"] = request.JobTitle;
            if (Normalize(existingContact.company_name) != Normalize(request.CompanyName))
                unmatchedFields["companyName"] = request.CompanyName;
            if (Normalize(existingContact.country_or_address) != Normalize(request.Location))
                unmatchedFields["location"] = request.Location;

            return new ExtensionOperationResult(200, new
            {
                matched = false,
                contactId = existingContact.id,
                dataFileId = existingContact.DataFileId,
                dataFileName = !string.IsNullOrWhiteSpace(existingContact.name)
                    ? existingContact.name
                    : existingContact.data_file_name,
                existingData = new
                {
                    contactName = existingContact.full_name,
                    jobTitle = existingContact.job_title,
                    companyName = existingContact.company_name,
                    location = existingContact.country_or_address
                },
                unmatchedData = unmatchedFields
            });
        }

        public async Task<ExtensionOperationResult> AddContactToDataFileAsync(AddContactToDataFileRequestDto request)
        {
            var dataFileExists = await _context.data_files.AsNoTracking().AnyAsync(df =>
                df.id == request.DataFileId && df.client_id == request.ClientId);

            if (!dataFileExists)
            {
                return new ExtensionOperationResult(404, new
                {
                    dataFileId = request.DataFileId,
                    message = "DataFile was not found for the given client."
                });
            }

            static string NormalizeUrl(string? value) =>
                (value ?? string.Empty).Trim().TrimEnd('/').ToLowerInvariant();

            var requestedLinkedInUrl = NormalizeUrl(request.LinkedInUrl);
            var existingUrls = await (
                from c in _context.contacts.AsNoTracking()
                join df in _context.data_files.AsNoTracking()
                    on c.DataFileId equals (int?)df.id
                where df.client_id == request.ClientId && c.linkedin_url != null
                select new { c.id, c.DataFileId, c.linkedin_url })
                .ToListAsync();
            var duplicate = existingUrls.FirstOrDefault(c =>
                NormalizeUrl(c.linkedin_url) == requestedLinkedInUrl);

            if (duplicate != null)
            {
                return new ExtensionOperationResult(409, new
                {
                    message = "This LinkedIn contact already exists in another Pitchkraft list.",
                    contactId = duplicate.id,
                    dataFileId = duplicate.DataFileId
                });
            }

            var contact = new Contact
            {
                DataFileId = request.DataFileId,
                full_name = request.ContactName?.Trim(),
                job_title = request.JobTitle?.Trim(),
                linkedin_url = request.LinkedInUrl!.Trim(),
                email = request.Email?.Trim(),
                company_name = request.CompanyName?.Trim(),
                country_or_address = request.Location?.Trim(),
                created_at = DateTime.UtcNow
            };

            _context.contacts.Add(contact);
            await _context.SaveChangesAsync();

            return new ExtensionOperationResult(200, new
            {
                success = true,
                contactId = contact.id,
                dataFileId = request.DataFileId
            });
        }

        public async Task<ExtensionOperationResult> UpdateContactFieldsAsync(UpdateContactFieldsRequestDto request)
        {
            var contact = await (
                from c in _context.contacts
                join df in _context.data_files on c.DataFileId equals (int?)df.id
                where c.id == request.ContactId &&
                      df.id == request.DataFileId &&
                      df.client_id == request.ClientId
                select c).FirstOrDefaultAsync();

            if (contact == null)
            {
                return new ExtensionOperationResult(404, new
                {
                    contactId = request.ContactId,
                    dataFileId = request.DataFileId,
                    message = "Contact was not found in the given client's DataFile."
                });
            }

            var updatedFields = new List<string>();
            if (request.ContactName != null)
            {
                contact.full_name = request.ContactName.Trim();
                updatedFields.Add("contactName");
            }
            if (request.JobTitle != null)
            {
                contact.job_title = request.JobTitle.Trim();
                updatedFields.Add("jobTitle");
            }
            if (request.LinkedInUrl != null)
            {
                contact.linkedin_url = request.LinkedInUrl.Trim();
                updatedFields.Add("linkedInUrl");
            }
            if (request.Email != null)
            {
                contact.email = request.Email.Trim();
                updatedFields.Add("email");
            }
            if (request.CompanyName != null)
            {
                contact.company_name = request.CompanyName.Trim();
                updatedFields.Add("companyName");
            }
            if (request.Location != null)
            {
                contact.country_or_address = request.Location.Trim();
                updatedFields.Add("location");
            }

            if (updatedFields.Count == 0)
            {
                return new ExtensionOperationResult(400,
                    new { message = "At least one field is required for update." });
            }

            contact.updated_at = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            return new ExtensionOperationResult(200, new
            {
                success = true,
                contactId = contact.id,
                dataFileId = contact.DataFileId,
                updatedFields
            });
        }

        public async Task<List<string>> GetEmailPatternsAsync(string domain)
        {
            if (string.IsNullOrWhiteSpace(domain))
                return new List<string>();

            var normalizedDomain = domain.Trim().ToLower();

            var domainId = await _context.Domain
                .Where(x => x.domain.ToLower() == normalizedDomain)
                .Select(x => (int?)x.id)
                .FirstOrDefaultAsync();

            if (!domainId.HasValue)
                return new List<string>();

            return await _context.EmailPattern
                .Where(x => x.DomainId == domainId.Value)
                .Select(x => x.EmailPatternName)
                .Distinct()
                .ToListAsync();
        }

        public string GenerateEmail(string name, string domain, string emailPattern)
        {
            if (string.IsNullOrWhiteSpace(name) ||
                string.IsNullOrWhiteSpace(domain) ||
                string.IsNullOrWhiteSpace(emailPattern))
            {
                return string.Empty;
            }

            var normalizedDomain = domain.Trim().ToLower();

            var email = emailPattern.Trim().ToLower() switch
            {
                "firstnameonly" => _calculateEmailRepository.FirstNameOnly(name, normalizedDomain),
                "firstnamedotlastname" => _calculateEmailRepository.FirstNamedotlastname(name, normalizedDomain),
                "firstinitialandlastname" => _calculateEmailRepository.FirstInitialandlastname(name, normalizedDomain),
                "firstinitialdotlastname" => _calculateEmailRepository.FirstInitialdotlastname(name, normalizedDomain),
                "firstinitialunderscorelastname" => _calculateEmailRepository.FirstInitialunderscorelastname(name, normalizedDomain),
                "firstnamedotlastinitial" => _calculateEmailRepository.FirstNamedotlastInitial(name, normalizedDomain),
                "firstnameandlastname" => _calculateEmailRepository.Firstnameandlastname(name, normalizedDomain),
                "firstnameunderscorelastinitial" => _calculateEmailRepository.FirstnameUnderscorelastInitial(name, normalizedDomain),
                "lastinitialdotfirstname" => _calculateEmailRepository.LastInitialdotfirstname(name, normalizedDomain),
                "lastinitialandfirstname" => _calculateEmailRepository.LastInitialAndfirstname(name, normalizedDomain),
                "lastinitialunderscorefirstname" => _calculateEmailRepository.LastInitialUnderscorefirstname(name, normalizedDomain),
                "lastnamedotfirstname" => _calculateEmailRepository.Lastnamedotfirstname(name, normalizedDomain),
                "lastnameunderscorefirstname" => _calculateEmailRepository.LastNameUnderscoreFirstName(name, normalizedDomain),
                "lastnameandfirstname" => _calculateEmailRepository.LastNameAndFirstName(name, normalizedDomain),
                "lastnamedotfirstinitial" => _calculateEmailRepository.LastNamedotFirstInitial(name, normalizedDomain),
                "lastnameunderscorefirstinitial" => _calculateEmailRepository.LastNameUnderscoreFirstInitial(name, normalizedDomain),
                "lastnameandfirstinitial" => _calculateEmailRepository.LastNameAndFirstInitial(name, normalizedDomain),
                "firstnameandlastinitial" => _calculateEmailRepository.FirstNameAndLastInitial(name, normalizedDomain),
                "firstnameunderscorelastname" => _calculateEmailRepository.FirstNameUnderscoreLastname(name, normalizedDomain),
                _ => string.Empty
            };

            return email.ToLower();
        }

        public async Task<string?> GetUnlockedEmailAsync(string domain, string? linkedInUrl)
        {
            if (string.IsNullOrWhiteSpace(domain) || string.IsNullOrWhiteSpace(linkedInUrl))
                return null;

            DateTime last30Days = DateTime.UtcNow.AddDays(-30);
            string normalizedDomain = "@" + domain.Trim().TrimStart('@').ToLowerInvariant();
            string normalizedLinkedInUrl = linkedInUrl.Trim();

            return await _context.UnlockedContacts
                .Where(x => x.LinkedInUrl == normalizedLinkedInUrl &&
                            x.EmailId != null &&
                            x.EmailId.ToLower().EndsWith(normalizedDomain) &&
                            x.UnlockedOn >= last30Days)
                .OrderByDescending(x => x.UnlockedOn)
                .Select(x => x.EmailId)
                .FirstOrDefaultAsync();
        }

        public async Task<string?> GetProspeoUnlockedEmailAsync(string linkedInUrl)
        {
            if (string.IsNullOrWhiteSpace(linkedInUrl))
                return null;

            var canonicalUrl = NormalizeLinkedInUrl(linkedInUrl);
            var withoutSlash = canonicalUrl.TrimEnd('/');
            var last30Days = DateTime.UtcNow.AddDays(-30);

            return await _context.UnlockedContacts
                .Where(x => (x.LinkedInUrl == canonicalUrl || x.LinkedInUrl == withoutSlash) &&
                            x.EmailId != null &&
                            x.EmailId != string.Empty &&
                            x.UnlockedOn >= last30Days)
                .OrderByDescending(x => x.UnlockedOn)
                .Select(x => x.EmailId)
                .FirstOrDefaultAsync();
        }

        public async Task<EmailVerificationResult> Stage2Async(string email,CancellationToken cancellationToken = default)
        {
            const int smtpTimeoutSeconds = 10;
            const string stage2Prefix = "Stage 2 - Verifying by MX. ";

            try
            {
                var emailAddress = new MailAddress(email);
                string host = emailAddress.Host;

                var lookupClient = new LookupClient();
                var lookupResult = await lookupClient.QueryAsync(
                    host,
                    QueryType.MX,
                    cancellationToken: cancellationToken);
                var mxRecords = lookupResult.Answers.MxRecords().ToList();

                if (mxRecords.Count == 0)
                {
                    return new EmailVerificationResult(
                        EmailVerificationState.Invalid,
                        stage2Prefix + "The domain has no MX record and cannot receive email.");
                }

                bool connectedToAnyMx = false;
                bool receivedDefinitiveRecipientRejection = false;
                bool receivedIndeterminateResponse = false;
                string? lastIndeterminateResponse = null;

                foreach (var mxRecord in mxRecords)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        using var tcpClient = new TcpClient();
                        await tcpClient
                            .ConnectAsync(mxRecord.Exchange.Value, 25, cancellationToken)
                            .AsTask()
                            .WaitAsync(TimeSpan.FromSeconds(smtpTimeoutSeconds), cancellationToken);
                        connectedToAnyMx = true;

                        using var networkStream = tcpClient.GetStream();
                        using var reader = new StreamReader(networkStream);
                        using var writer = new StreamWriter(networkStream)
                        {
                            AutoFlush = true,
                            NewLine = "\r\n"
                        };

                        string? bannerResponse = await ReadLineWithTimeoutAsync(reader, cancellationToken);
                        if (GetResponseCode(bannerResponse ?? string.Empty) != 220)
                        {
                            receivedIndeterminateResponse = true;
                            lastIndeterminateResponse = bannerResponse;
                            continue;
                        }

                        await writer.WriteLineAsync("HELO pitchcraft.local");
                        string? heloResponse = await ReadLineWithTimeoutAsync(reader, cancellationToken);
                        if (GetResponseCode(heloResponse ?? string.Empty) != 250)
                        {
                            receivedIndeterminateResponse = true;
                            lastIndeterminateResponse = heloResponse;
                            continue;
                        }

                        await writer.WriteLineAsync("MAIL FROM:<oliver@datagenie.email>");
                        string? mailFromResponse = await ReadLineWithTimeoutAsync(reader, cancellationToken);
                        if (GetResponseCode(mailFromResponse ?? string.Empty) != 250)
                        {
                            receivedIndeterminateResponse = true;
                            lastIndeterminateResponse = mailFromResponse;
                            continue;
                        }

                        await writer.WriteLineAsync($"RCPT TO:<{email}>");
                        string? response = await ReadLineWithTimeoutAsync(reader, cancellationToken);
                        int responseCode = GetResponseCode(response ?? string.Empty);

                        Console.WriteLine(
                            $"[EmailUnlock][SMTP] Email={email}, MX={mxRecord.Exchange.Value}, " +
                            $"ResponseCode={responseCode}, Response={response}");

                        await writer.WriteLineAsync("QUIT");

                        if (responseCode is 250 or 251)
                        {
                            return new EmailVerificationResult(
                                EmailVerificationState.Valid,
                                stage2Prefix + "The recipient was accepted by the mail server.");
                        }

                        if (IsDefinitiveMailboxNotFound(responseCode, response))
                        {
                            receivedDefinitiveRecipientRejection = true;
                            continue;
                        }

                        // 4xx, policy/auth failures and unknown SMTP responses do not prove
                        // that the mailbox is invalid.
                        receivedIndeterminateResponse = true;
                        lastIndeterminateResponse = response;
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        // This MX timed out. Try another MX before reporting unavailable.
                        receivedIndeterminateResponse = true;
                    }
                    catch (TimeoutException)
                    {
                        // This MX timed out. Try another MX before reporting unavailable.
                        receivedIndeterminateResponse = true;
                    }
                    catch (SocketException)
                    {
                        // Port 25 can be blocked by the hosting network.
                        receivedIndeterminateResponse = true;
                    }
                    catch (IOException)
                    {
                        // The remote SMTP server closed or did not complete the conversation.
                        receivedIndeterminateResponse = true;
                    }
                }

                if (!connectedToAnyMx || receivedIndeterminateResponse)
                {
                    return new EmailVerificationResult(
                        EmailVerificationState.VerificationUnavailable,
                        stage2Prefix +
                        "Verification is unavailable because the MX server could not be reached " +
                        "or returned a temporary/indeterminate response. The email was not marked invalid." +
                        FormatSmtpReason(lastIndeterminateResponse));
                }

                if (receivedDefinitiveRecipientRejection)
                {
                    return new EmailVerificationResult(
                        EmailVerificationState.Invalid,
                        stage2Prefix +
                        "The mail server explicitly reported that the mailbox does not exist (5.1.1/5.1.10)." );
                }

                return new EmailVerificationResult(
                    EmailVerificationState.VerificationUnavailable,
                    stage2Prefix +
                    "The SMTP conversation did not provide a conclusive recipient result. " +
                    "The email was not marked invalid.");
            }
            catch (FormatException)
            {
                return new EmailVerificationResult(
                    EmailVerificationState.Invalid,
                    stage2Prefix + "The generated email format is invalid.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is DnsClient.DnsResponseException or SocketException or IOException or TimeoutException)
            {
                return new EmailVerificationResult(
                    EmailVerificationState.VerificationUnavailable,
                    stage2Prefix +
                    "Verification is temporarily unavailable due to a DNS/network error. " +
                    "The email was not marked invalid.");
            }
        }

        

        public async Task<EmailVerificationResult> Stage3Async(string email, string firstName, string? contactId, int clientId, CancellationToken cancellationToken = default)
        {
            const string prefix = "Stage 3 - Verifying by sending an actual email. ";
            var smtpCredential = GetConfiguredStage3Smtp();

            if (smtpCredential == null)
            {
                return new EmailVerificationResult(
                    EmailVerificationState.VerificationUnavailable,
                    prefix +
                    "The application Stage 3 SMTP is not configured in EmailUnlockStage3:Smtp.");
            }

            DateTime sentAt = DateTime.UtcNow;
            string messageId = $"<{MimeUtils.GenerateMessageId().Trim('<', '>')}>";
            var template = await _context.EmailTemplates
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.TemplateName == "EmailUnlockStage3", cancellationToken);

            if (template == null ||
                string.IsNullOrWhiteSpace(template.Subject) ||
                string.IsNullOrWhiteSpace(template.Body))
            {
                return new EmailVerificationResult(
                    EmailVerificationState.VerificationUnavailable,
                    prefix +
                    "The EmailUnlockStage3 subject/body template is missing or empty in EmailTemplates.");
            }

            var placeholders = new Dictionary<string, string>
            {
                ["first_name"] = firstName,
            };
            string subject = _emailTemplateHelper.ReplacePlaceholders(
                template.Subject,
                placeholders);
            string body = _emailTemplateHelper.ReplacePlaceholders(
                template.Body,
                placeholders);
            try
            {
                var message = new MimeMessage();
                message.MessageId = messageId.Trim('<', '>');
                message.From.Add(new MailboxAddress(
                    smtpCredential.SenderName ?? "Pitchkraft",
                    smtpCredential.FromEmail));
                message.To.Add(MailboxAddress.Parse(email));
                message.Subject = subject;
                message.Body = new BodyBuilder { HtmlBody = body }.ToMessageBody();

                using var smtpClient = new MailKit.Net.Smtp.SmtpClient();
                await smtpClient.ConnectAsync(
                    smtpCredential.Server,
                    smtpCredential.Port,
                    GetStage3SocketOption(smtpCredential.SecurityType, smtpCredential.UseSsl),
                    cancellationToken);

                if (!string.IsNullOrWhiteSpace(smtpCredential.Username))
                {
                    await smtpClient.AuthenticateAsync(
                        smtpCredential.Username,
                        smtpCredential.Password,
                        cancellationToken);
                }

                await smtpClient.SendAsync(message, cancellationToken);
                await smtpClient.DisconnectAsync(true, cancellationToken);

                Console.WriteLine($"[EmailUnlock][Stage3] Verification email sent to {email}; MessageId={messageId}");

                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);

                bool? bounced = await CheckStage3BounceDirectlyAsync(
                    email,
                    messageId,
                    sentAt,
                    cancellationToken);

                if (!bounced.HasValue)
                {
                    return new EmailVerificationResult(
                        EmailVerificationState.VerificationUnavailable,
                        prefix + "The Stage 3 IMAP inbox is not configured, so bounce verification could not run.");
                }

                if (bounced.Value)
                {
                    Console.WriteLine($"[EmailUnlock][Stage3] Bounce detected for {email}");
                    return new EmailVerificationResult(
                        EmailVerificationState.Invalid,
                        prefix + "A delivery bounce was detected.");
                }

                return new EmailVerificationResult(
                    EmailVerificationState.Valid,
                    prefix + "Email sent successfully and no immediate bounce was detected.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EmailUnlock][Stage3] Failed for {email}: {ex.Message}");
                return new EmailVerificationResult(
                    EmailVerificationState.VerificationUnavailable,
                    prefix + $"The verification email could not be sent: {ex.Message}");
            }
        }

        

        public async Task<bool> CompleteUnlockAsync(string? contactId, int clientId, string? linkedInUrl, string email, string name, string domain)
        {
            var executionStrategy = _context.Database.CreateExecutionStrategy();

            return await executionStrategy.ExecuteAsync(async () =>
            {
                await using var transaction = await _context.Database.BeginTransactionAsync();
                try
                {
                    if (!await _contactRepository.CreditDeduction(clientId))
                    {
                        await transaction.RollbackAsync();
                        return false;
                    }

                    _context.UnlockedContacts.Add(new Model.UnlockedContacts
                    {
                        ClientId = clientId.ToString(),
                        ContactId = contactId?.Trim() ?? string.Empty,
                        EmailId = email,
                        LinkedInUrl = linkedInUrl?.Trim() ?? string.Empty,
                        UnlockedOn = DateTime.UtcNow
                    });

                    await SaveLearnedPatternAsync(name, email, domain);
                    await _context.SaveChangesAsync();
                    await transaction.CommitAsync();
                    return true;
                }
                catch
                {
                    await transaction.RollbackAsync();
                    throw;
                }
            });
        }

        public async Task<bool> CompleteProspeoUnlockAsync(
            string? contactId,
            int clientId,
            string linkedInUrl,
            string email)
        {
            var executionStrategy = _context.Database.CreateExecutionStrategy();

            return await executionStrategy.ExecuteAsync(async () =>
            {
                await using var transaction = await _context.Database.BeginTransactionAsync();
                try
                {
                    if (!await _contactRepository.CreditDeduction(clientId))
                    {
                        await transaction.RollbackAsync();
                        return false;
                    }

                    var canonicalUrl = NormalizeLinkedInUrl(linkedInUrl);
                    var withoutSlash = canonicalUrl.TrimEnd('/');
                    var clientIdText = clientId.ToString();
                    var existing = await _context.UnlockedContacts
                        .Where(x => x.LinkedInUrl == canonicalUrl || x.LinkedInUrl == withoutSlash)
                        .OrderByDescending(x => x.UnlockedOn)
                        .FirstOrDefaultAsync();

                    if (existing == null)
                    {
                        _context.UnlockedContacts.Add(new Model.UnlockedContacts
                        {
                            ClientId = clientIdText,
                            ContactId = contactId?.Trim() ?? string.Empty,
                            EmailId = email.Trim(),
                            LinkedInUrl = canonicalUrl,
                            UnlockedOn = DateTime.UtcNow
                        });
                    }
                    else
                    {
                        existing.EmailId = email.Trim();
                        existing.LinkedInUrl = canonicalUrl;
                        existing.UnlockedOn = DateTime.UtcNow;
                    }

                    await _context.SaveChangesAsync();
                    await transaction.CommitAsync();
                    return true;
                }
                catch
                {
                    await transaction.RollbackAsync();
                    throw;
                }
            });
        }

        /// <summary>
        /// Drops the address off the cached unlock for this profile, leaving the
        /// row itself in place so the unlock history and its credit are still on
        /// record. Called when a forced refresh found nothing: without it the
        /// next plain unlock would hand back the very address the caller had
        /// just rejected.
        /// </summary>
        public async Task<bool> ClearProspeoUnlockedEmailAsync(string linkedInUrl, string email)
        {
            if (string.IsNullOrWhiteSpace(linkedInUrl) || string.IsNullOrWhiteSpace(email))
                return false;

            var canonicalUrl = NormalizeLinkedInUrl(linkedInUrl);
            var withoutSlash = canonicalUrl.TrimEnd('/');
            var rejected = email.Trim();

            var rows = await _context.UnlockedContacts
                .Where(x => (x.LinkedInUrl == canonicalUrl || x.LinkedInUrl == withoutSlash) &&
                            x.EmailId == rejected)
                .ToListAsync();

            if (rows.Count == 0)
                return false;

            foreach (var row in rows)
                row.EmailId = string.Empty;

            await _context.SaveChangesAsync();
            return true;
        }

        //------------------------------------------------------------------------Private Mathods---------------------------------------------------------------------------------

        private static string NormalizeLinkedInUrl(string value)
        {
            var input = value.Trim();
            if (!Uri.TryCreate(input, UriKind.Absolute, out var uri))
                return input.TrimEnd('/') + "/";

            return $"{uri.Scheme.ToLowerInvariant()}://{uri.Host.ToLowerInvariant()}" +
                   uri.AbsolutePath.TrimEnd('/') + "/";
        }

        private int GetResponseCode(string responseString)
        {
            if (string.IsNullOrWhiteSpace(responseString) || responseString.Length < 3)
                return 0;

            return int.TryParse(responseString.Substring(0, 3), out int code)
                ? code
                : 0;
        }

        private async Task SaveLearnedPatternAsync(string name, string email, string domain)
        {
            string pattern = _calculateEmailRepository.FindEmailPattern(name, email);
            if (string.IsNullOrWhiteSpace(pattern) || string.IsNullOrWhiteSpace(domain))
                return;

            string normalizedDomain = domain.Trim().ToLowerInvariant();
            var domainEntity = await _context.Domain
                .FirstOrDefaultAsync(x => x.domain.ToLower() == normalizedDomain);

            if (domainEntity == null)
            {
                domainEntity = new Model.Domain { domain = normalizedDomain };
                _context.Domain.Add(domainEntity);
                await _context.SaveChangesAsync();
            }

            bool patternExists = await _context.EmailPattern.AnyAsync(x =>
                x.DomainId == domainEntity.id && x.EmailPatternName == pattern);

            if (!patternExists)
            {
                _context.EmailPattern.Add(new Model.EmailPattern
                {
                    DomainId = domainEntity.id,
                    EmailPatternName = pattern
                });
            }
        }

        private static string NormalizeMessageId(string? value) =>
           (value ?? string.Empty).Trim().Trim('<', '>').ToLowerInvariant();

        private async Task<bool?> CheckStage3BounceDirectlyAsync(string recipientEmail, string originalMessageId, DateTime sentAt, CancellationToken cancellationToken)
        {
            var section = _configuration.GetSection("EmailUnlockStage3:Inbox");
            string? host = section["Host"];
            string? username = section["Username"];
            string? password = section["Password"];

            if (string.IsNullOrWhiteSpace(host) ||
                string.IsNullOrWhiteSpace(username) ||
                string.IsNullOrWhiteSpace(password))
            {
                return null;
            }

            int port = int.TryParse(section["Port"], out int configuredPort)
                ? configuredPort
                : 993;
            var socketOption = GetStage3SocketOption(
                section["SecurityType"] ?? "SSL/TLS",
                true);

            using var imapClient = new ImapClient();
            await imapClient.ConnectAsync(host, port, socketOption, cancellationToken);
            await imapClient.AuthenticateAsync(username, password, cancellationToken);
            await imapClient.Inbox.OpenAsync(MailKit.FolderAccess.ReadOnly, cancellationToken);

            var messageUids = await imapClient.Inbox.SearchAsync(
                SearchQuery.DeliveredAfter(sentAt.AddMinutes(-2)),
                cancellationToken);

            foreach (var uid in messageUids.Reverse().Take(25))
            {
                var inboxMessage = await imapClient.Inbox.GetMessageAsync(uid, cancellationToken);
                string body = inboxMessage.TextBody ?? inboxMessage.HtmlBody ?? string.Empty;
                var bounce = BounceParser.Parse(new BounceMailInput
                {
                    FromEmail = inboxMessage.From.Mailboxes.FirstOrDefault()?.Address,
                    Subject = inboxMessage.Subject,
                    Body = body,
                    HeadersText = inboxMessage.Headers.ToString(),
                    Provider = "IMAP"
                });

                if (!bounce.IsBounce)
                    continue;

                bool recipientMatches = string.Equals(
                    bounce.RecipientEmail,
                    recipientEmail,
                    StringComparison.OrdinalIgnoreCase);
                bool messageMatches = NormalizeMessageId(bounce.OriginalMessageId) ==
                                      NormalizeMessageId(originalMessageId);
                bool bodyMatches = body.Contains(recipientEmail, StringComparison.OrdinalIgnoreCase);

                if (recipientMatches || messageMatches || bodyMatches)
                {
                    await imapClient.DisconnectAsync(true, cancellationToken);
                    return true;
                }
            }

            await imapClient.DisconnectAsync(true, cancellationToken);
            return false;
        }

        private static SecureSocketOptions GetStage3SocketOption(string? securityType, bool useSsl) =>
            securityType?.ToUpperInvariant() switch
            {
                "SSL/TLS" => SecureSocketOptions.SslOnConnect,
                "STARTTLS" => SecureSocketOptions.StartTls,
                "NONE" => SecureSocketOptions.None,
                "AUTO" => SecureSocketOptions.Auto,
                _ => useSsl ? SecureSocketOptions.Auto : SecureSocketOptions.None
            };

        private Model.SmtpCredentials? GetConfiguredStage3Smtp()
        {
            var section = _configuration.GetSection("EmailUnlockStage3:Smtp");
            string? server = section["Server"];
            string? username = section["Username"];
            string? password = section["Password"];
            string? fromEmail = section["FromEmail"];

            if (string.IsNullOrWhiteSpace(server) ||
                string.IsNullOrWhiteSpace(username) ||
                string.IsNullOrWhiteSpace(password) ||
                string.IsNullOrWhiteSpace(fromEmail))
            {
                return null;
            }

            return new Model.SmtpCredentials
            {
                Id = 0,
                ClientId = "Stage3",
                Server = server,
                Port = int.TryParse(section["Port"], out int port) ? port : 587,
                Username = username,
                Password = password,
                FromEmail = fromEmail,
                SenderName = section["SenderName"] ?? "Pitchkraft",
                SecurityType = section["SecurityType"] ?? "STARTTLS",
                UseSsl = !bool.TryParse(section["UseSsl"], out bool useSsl) || useSsl
            };
        }

        private static async Task<string?> ReadLineWithTimeoutAsync(StreamReader reader, CancellationToken cancellationToken)
        {
            return await reader.ReadLineAsync(cancellationToken)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        }

        private static bool IsDefinitiveMailboxNotFound(int responseCode, string? response)
        {
            if (responseCode is not (550 or 551 or 553) || string.IsNullOrWhiteSpace(response))
                return false;

            return response.Contains("5.1.1", StringComparison.OrdinalIgnoreCase) ||
                   response.Contains("5.1.10", StringComparison.OrdinalIgnoreCase);
        }

        private static string FormatSmtpReason(string? response) =>
            string.IsNullOrWhiteSpace(response)
                ? string.Empty
                : $" SMTP response: {response.Trim()}";

        private static readonly string[] AllEmailPatterns =
        {
            "FirstNameOnly",
            "FirstNamedotlastname",
            "FirstInitialandlastname",
            "FirstInitialdotlastname",
            "FirstInitialunderscorelastname",
            "FirstNamedotlastInitial",
            "Firstnameandlastname",
            "FirstnameUnderscorelastInitial",
            "LastInitialdotfirstname",
            "LastInitialAndfirstname",
            "LastInitialUnderscorefirstname",
            "Lastnamedotfirstname",
            "LastNameUnderscoreFirstName",
            "LastNameAndFirstName",
            "LastNamedotFirstInitial",
            "LastNameUnderscoreFirstInitial",
            "LastNameAndFirstInitial",
            "FirstNameAndLastInitial",
            "FirstNameUnderscoreLastname"
        };
    }
}
