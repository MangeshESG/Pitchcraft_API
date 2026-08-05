using DnsClient;
using Microsoft.EntityFrameworkCore;
using PitchGenApi.Database;
using PitchGenApi.Interfaces;
using System.Net.Mail;
using System.Net.Sockets;

namespace PitchGenApi.Repository
{
    public class ExtensionRepository : IExtensionRepository
    {
        private readonly AppDbContext _context;
        private readonly CalculateEmailRepository _calculateEmailRepository;

        public ExtensionRepository(
            AppDbContext context,
            CalculateEmailRepository calculateEmailRepository)
        {
            _context = context;
            _calculateEmailRepository = calculateEmailRepository;
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
        public string GetUnlockedEmail(string linkedInUrl)
        {
            DateTime last30Days = DateTime.Now.AddDays(-30);

            return _context.UnlockedContacts
                .Where(x => x.LinkedInUrl == linkedInUrl &&
                            x.UnlockedOn >= last30Days)
                .OrderByDescending(x => x.UnlockedOn)
                .Select(x => x.EmailId)
                .FirstOrDefault();
        }

        public async Task<(bool IsValid, string Stage2Status)> Stage2Async(string email)
        {
            string stage2Status = "Stage 2 - Verifying by MX \n";

            try
            {
                var emailAddress = new MailAddress(email);
                string host = emailAddress.Host;

                var lookupClient = new LookupClient();
                var lookupResult = await lookupClient.QueryAsync(host, QueryType.MX);

                foreach (var mxRecord in lookupResult.Answers.MxRecords())
                {
                    using var tcpClient = new TcpClient();

                    await tcpClient.ConnectAsync(mxRecord.Exchange.Value, 25);

                    using var networkStream = tcpClient.GetStream();
                    using var reader = new StreamReader(networkStream);
                    using var writer = new StreamWriter(networkStream)
                    {
                        AutoFlush = true,
                        NewLine = "\r\n"
                    };

                    // SMTP Banner
                    await reader.ReadLineAsync();

                    // HELO
                    await writer.WriteLineAsync("HELO ShishirHere");
                    await reader.ReadLineAsync();

                    // MAIL FROM
                    await writer.WriteLineAsync("MAIL FROM:<oliver@datagenie.email>");
                    await reader.ReadLineAsync();

                    // RCPT TO
                    await writer.WriteLineAsync($"RCPT TO:<{email}>");
                    string? response = await reader.ReadLineAsync();

                    if (!string.IsNullOrWhiteSpace(response) &&
                        GetResponseCode(response) == 250)
                    {
                        await writer.WriteLineAsync("QUIT");

                        stage2Status += "Stage 2 completed successfully\n";
                        return (true, stage2Status);
                    }

                    await writer.WriteLineAsync("QUIT");
                }

                stage2Status += "Failed at stage 2 after multiple retry\n";
                stage2Status += "AI testing failed";

                return (false, stage2Status);
            }
            catch (SmtpException)
            {
                stage2Status += "Failed at stage 2 after multiple retry\n";
                stage2Status += "AI testing failed";

                return (false, stage2Status);
            }
            catch (Exception)
            {
                stage2Status += "Failed at stage 2 after multiple retry\n";
                stage2Status += "AI testing failed";

                return (false, stage2Status);
            }
        }

        public async Task UpdateContactEmailAsync(string linkedInUrl, string email, int clientId)
        {
            try
            {
                var contact = await (
                    from c in _context.contacts
                    join d in _context.data_files
                        on c.DataFileId equals d.id
                    where c.linkedin_url == linkedInUrl
                          && d.client_id == clientId
                    select c
                ).FirstOrDefaultAsync();

                if (contact != null)
                {
                    contact.email = email;
                    await _context.SaveChangesAsync();
                }
            }
            catch (Exception ex)
            {
                throw new Exception("Error while updating contact email.", ex);
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
