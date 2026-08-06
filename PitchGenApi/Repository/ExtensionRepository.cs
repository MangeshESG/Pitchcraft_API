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

        public ExtensionRepository(
            AppDbContext context,
            CalculateEmailRepository calculateEmailRepository,
            ContactRepository contactRepository,
            EmailTemplateHelper emailTemplateHelper,
            IConfiguration configuration)
        {
            _context = context;
            _calculateEmailRepository = calculateEmailRepository;
            _contactRepository = contactRepository;
            _emailTemplateHelper = emailTemplateHelper;
            _configuration = configuration;
        }

        public IReadOnlyList<string> GetAllEmailPatterns() => AllEmailPatterns;

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
        public async Task<string?> GetUnlockedEmailAsync(
            string contactId,
            int clientId,
            string? linkedInUrl)
        {
            DateTime last30Days = DateTime.UtcNow.AddDays(-30);
            string normalizedClientId = clientId.ToString();
            string normalizedContactId = contactId.Trim();

            return await _context.UnlockedContacts
                .Where(x => x.ClientId == normalizedClientId &&
                            (x.ContactId == normalizedContactId ||
                             (!string.IsNullOrWhiteSpace(linkedInUrl) && x.LinkedInUrl == linkedInUrl)) &&
                            x.UnlockedOn >= last30Days)
                .OrderByDescending(x => x.UnlockedOn)
                .Select(x => x.EmailId)
                .FirstOrDefaultAsync();
        }

        public async Task<EmailVerificationResult> Stage2Async(
            string email,
            CancellationToken cancellationToken = default)
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

        private static async Task<string?> ReadLineWithTimeoutAsync(
            StreamReader reader,
            CancellationToken cancellationToken)
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

        public async Task<EmailVerificationResult> Stage3Async(
            string email,
            string firstName,
            string contactId,
            int clientId,
            CancellationToken cancellationToken = default)
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
                ["FirstName"] = firstName,
                ["UserEmail"] = email,
                ["ContactID"] = contactId,
                ["DateTime"] = DateTime.UtcNow.ToString("dd MMM yyyy, hh:mm tt 'UTC'")
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

        private async Task<bool?> CheckStage3BounceDirectlyAsync(
            string recipientEmail,
            string originalMessageId,
            DateTime sentAt,
            CancellationToken cancellationToken)
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

        private static string NormalizeMessageId(string? value) =>
            (value ?? string.Empty).Trim().Trim('<', '>').ToLowerInvariant();

        public async Task<bool> CompleteUnlockAsync(
            string contactId,
            int clientId,
            string? linkedInUrl,
            string email,
            string name,
            string domain)
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

                    int? parsedContactId = int.TryParse(contactId, out int id) ? id : null;
                    var contact = await (
                        from c in _context.contacts
                        join d in _context.data_files
                            on c.DataFileId equals d.id
                        where d.client_id == clientId &&
                              ((parsedContactId.HasValue && c.id == parsedContactId.Value) ||
                               (!string.IsNullOrWhiteSpace(linkedInUrl) && c.linkedin_url == linkedInUrl))
                        select c
                    ).FirstOrDefaultAsync();

                    if (contact == null)
                        throw new InvalidOperationException("Contact was not found for this client.");

                    contact.email = email;
                    contact.updated_at = DateTime.UtcNow;

                    _context.UnlockedContacts.Add(new Model.UnlockedContacts
                    {
                        ClientId = clientId.ToString(),
                        ContactId = contactId.Trim(),
                        EmailId = email,
                        LinkedInUrl = linkedInUrl ?? contact.linkedin_url ?? string.Empty,
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
        //------------------------------------------------------------------------Private Mathods---------------------------------------------------------------------------------

        private int GetResponseCode(string responseString)
        {
            if (string.IsNullOrWhiteSpace(responseString) || responseString.Length < 3)
                return 0;

            return int.TryParse(responseString.Substring(0, 3), out int code)
                ? code
                : 0;
        }
    }
}
