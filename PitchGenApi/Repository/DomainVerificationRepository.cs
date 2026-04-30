using DnsClient;
using DnsClient.Protocol;
using Microsoft.EntityFrameworkCore;
using PitchGenApi.Database;
using PitchGenApi.Helpers;
using PitchGenApi.Interfaces;
using PitchGenApi.Model;
using PitchGenApi.Model.DTOs;
using System.Security.Cryptography;
using System.Text.Json;

namespace PitchGenApi.Repositories
{
    public class DomainVerificationRepository : IDomainVerificationRepository
    {
        private readonly AppDbContext _db;
        private readonly IRegisterEmailSender _reg;

        public DomainVerificationRepository(AppDbContext db, IRegisterEmailSender reg)
        {
            _db = db;
            _reg = reg;
        }

        // ================================
        // Generate Token + Add Email
        // ================================
        //public async Task<OperationResult> GenerateToken( string email, int clientId, SmtpCredentialDto dto, string ip, string browsername)
        //{
        //    var strategy = _db.Database.CreateExecutionStrategy();

        //    return await strategy.ExecuteAsync(async () =>
        //    {
        //        try
        //        {
        //            //if (string.IsNullOrWhiteSpace(email) || !email.Contains("@"))
        //            //    return Fail("Invalid email format");

        //            //string domain = email.Split('@')[1].Trim().ToLower();

        //            //var domainRecord = await _db.DomainVerification
        //            //    .FirstOrDefaultAsync(x => x.Domain == domain && x.ClientId == clientId);

        //            //if (domainRecord == null)
        //            //{
        //            //    domainRecord = new DomainVerification
        //            //    {
        //            //        ClientId = clientId,
        //            //        Domain = domain,
        //            //        VerificationToken = GenerateTokenValue(),
        //            //        IsVerified = false,
        //            //        CreatedAt = DateTime.UtcNow
        //            //    };

        //            //    await _db.DomainVerification.AddAsync(domainRecord);
        //            //    await _db.SaveChangesAsync();
        //            //}
        //            //else if (!domainRecord.IsVerified)
        //            //{
        //            //    if (string.IsNullOrEmpty(domainRecord.VerificationToken))
        //            //    {
        //            //        domainRecord.VerificationToken = GenerateTokenValue();
        //            //        await _db.SaveChangesAsync();
        //            //    }
        //            //}

        //            return await AddEmailForDomain(
        //                clientId,
        //                email,
        //                dto,
        //                ip,
        //                browsername
        //            );
        //        }
        //        catch (Exception ex)
        //        {
        //            return Fail(ex.Message);
        //        }
        //    });
        //}

        // ================================
        // Verify Domain via DNS
        // ================================
        public async Task<OperationResult> VerifyDomain(string domain, int clientId)
        {
            try
            {
                domain = domain?.Trim().ToLower();

                var record = await _db.DomainVerification
                    .FirstOrDefaultAsync(x => x.Domain == domain && x.ClientId == clientId);

                if (record == null)
                    return Fail("Domain not found");

                if (record.IsVerified)
                    return Success("Domain already verified");

                bool tokenFound = await CheckTxtRecord(
                    $"_pitchgen.{domain}",
                    $"pitchgen-verification={record.VerificationToken}"
                );

                if (!tokenFound)
                    return Fail("DNS record not found or not propagated yet");

                record.IsVerified = true;
                record.VerifiedAt = DateTime.UtcNow;

                await _db.SaveChangesAsync();

                return Success("Domain verified successfully");
            }
            catch
            {
                return Fail("Failed to verify domain");
            }
        }

        // ================================
        // Add Email for Domain + OTP
        // ================================
        public async Task<OperationResult> AddEmailForDomain(
    int clientId,
    string email,
    SmtpCredentialDto dto,
    string ip,
    string browsername)
        {
            try
            {
                var user = await _db.ClientDetails
                    .FirstOrDefaultAsync(x => x.Id == clientId);

                if (!dto.IsUpdate)
                {
                    string otp = OtpGenerator.GenerateSecureOtp();
                    var smtpdetails = JsonSerializer.Serialize(dto);

                    var otpEntity = new EmailOtpVerification
                    {
                        Email = email,
                        OTP = otp,
                        username = email,
                        IsVerified = false,
                        OtpType = "DomainVerify",
                        CreatedAt = DateTime.UtcNow,
                        ExpiresAt = DateTime.UtcNow.AddMinutes(10),
                        TempSmtpPayload = smtpdetails
                    };

                    await _db.EmailOtpVerifications.AddAsync(otpEntity);

                    // no Task.Run
                    await _reg.DomainVerifyOTP(
                        email,
                        otp,
                        user.FirstName,
                        ip,
                        browsername,
                        email
                    );
                }
                else
                {
                    var smtpupdate = await _db.SmtpCredentials
                        .FirstOrDefaultAsync(x =>
                            x.ClientId == clientId.ToString() &&
                            x.FromEmail == dto.FromEmail);

                    if (smtpupdate == null)
                        return Fail("SMTP record not found");

                    smtpupdate.Server = dto.Server;
                    smtpupdate.Port = dto.Port;
                    smtpupdate.Username = dto.Username;
                    smtpupdate.Password = dto.Password;
                    smtpupdate.SenderName = dto.SenderName;
                    smtpupdate.UseSsl = dto.UseSsl;
                    smtpupdate.SecurityType = dto.SecurityType;
                }

                await _db.SaveChangesAsync();

                return Success("Prepared for verification");
            }
            catch (Exception ex)
            {
                return Fail(ex.Message);
            }
        }

        //public async Task<OperationResult> ResendDomainVerifyOTP(int domainemaiId, int clientId)
        //{
        //    try
        //    {
        //        var client = await _db.ClientDetails
        //            .FirstOrDefaultAsync(x => x.Id == clientId);

        //        var domainEmail = await _db.DomainEmailVerification
        //               .FirstOrDefaultAsync(x => x.Id == domainemaiId && x.ClientId == clientId);

        //        string otp = OtpGenerator.GenerateSecureOtp();

        //        var otpEntity = new EmailOtpVerification
        //        {
        //            Email = domainEmail.Email,
        //            OTP = otp,
        //            username = domainEmail.Email,
        //            IsVerified = false,
        //            OtpType = "DomainVerify",
        //            CreatedAt = DateTime.UtcNow,
        //            ExpiresAt = DateTime.UtcNow.AddMinutes(10)
        //        };

        //        await _db.EmailOtpVerifications.AddAsync(otpEntity);
        //        await _db.SaveChangesAsync();

        //        // 📤 Send OTP email
        //        await _reg.DomainVerifyOTP(domainEmail.Email, otp, client.FirstName);

        //        return Success("Email added and OTP sent successfully");
        //    }
        //    catch
        //    {
        //        return Fail("Failed to add email for domain");
        //    }
        //}

        //public async Task<OperationResult> DomainVerifyEmailOTP(string email, string otp, int clientId)
        //{
        //    try
        //    {
        //        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(otp))
        //            return Fail("Email or OTP is invalid");

        //        // Get latest valid OTP
        //        var otpEntry = await _db.EmailOtpVerifications
        //            .Where(x =>
        //                x.Email == email &&
        //                x.OTP == otp &&
        //                !x.IsVerified &&
        //                x.ExpiresAt > DateTime.UtcNow &&
        //                x.OtpType == "DomainVerify")
        //            .OrderByDescending(x => x.CreatedAt)
        //            .FirstOrDefaultAsync();

        //        if (otpEntry == null)
        //            return Fail("Invalid or expired OTP");

        //        // Mark OTP as verified
        //        otpEntry.IsVerified = true;

        //        // Optional: mark domain email as verified
        //        var domainEmail = await _db.DomainEmailVerification
        //            .FirstOrDefaultAsync(x => x.Email == email && x.ClientId == clientId);

        //        if (domainEmail == null)
        //            return Fail("Domain email not found");

        //        domainEmail.IsEmailVerified = true;
        //        domainEmail.EmailVerifiedAt = DateTime.UtcNow;

        //        await _db.SaveChangesAsync();

        //        return Success("Email verified successfully");
        //    }
        //    catch (Exception ex)
        //    {
        //        // You can log ex here
        //        return Fail("Failed to verify email OTP");
        //    }
        //}

        public async Task<OperationResult> VerifySpfDkimDmarc(string email, string clientId)
        {
            try
            {
                int userId = int.TryParse(clientId, out var id) ? id : 0;
                var smtp = await _db.SmtpCredentials
                    .FirstOrDefaultAsync(x => x.FromEmail == email && x.ClientId == clientId);

                if (smtp == null)
                    return Fail("SMTP settings not found.");

                var domain = GetDomain(smtp.FromEmail);

                if (string.IsNullOrWhiteSpace(domain))
                    return Fail("Invalid sender email domain.");

                // ===== SPF =====
                var spfRecords = await GetSpfRecords(domain);
                bool spfOk = IsSmtpAllowedInSpf(spfRecords, smtp.Server);

                // ===== DKIM =====
                bool dkimOk = await HasDkimRecord(domain);

                // ===== DMARC =====
                bool dmarcOk = await HasDmarcRecord(domain);

                // ===== Save / Update Verification =====
                var verification = await _db.DomainVerification
                    .FirstOrDefaultAsync(x => x.Domain == domain && x.ClientId == userId);

                if (verification == null)
                {
                    verification = new DomainVerification
                    {
                        Domain = domain,
                        ClientId = userId
                    };
                    _db.DomainVerification.Add(verification);
                }

                verification.IsSpfVerified = spfOk;
                verification.IsDkimVerified = dkimOk;
                verification.IsDmarcVerified = dmarcOk;

                await _db.SaveChangesAsync();

                // ===== Result Message =====
                if (spfOk && dkimOk && dmarcOk)
                {
                    return Success("SPF, DKIM, and DMARC are verified successfully.");
                }

                var failed = new List<string>();
                if (!spfOk) failed.Add("SPF");
                if (!dkimOk) failed.Add("DKIM");
                if (!dmarcOk) failed.Add("DMARC");

                return Success($"Verification completed with issues. Failed: {string.Join(", ", failed)}.");
            }
            catch (Exception ex)
            {
                // optional logging
                // _logger.LogError(ex, "DNS verification failed");

                return Fail("DNS verification failed. Please try again later.");
            }
        }

        public async Task<OperationResult> GetVerifiedDomain(int clientId)
        {
            try
            {
                // 1️⃣ Domains (multiple domains allowed per client)
                var domains = await _db.DomainVerification
                    .Where(d => d.ClientId == clientId)
                    .Select(d => new
                    {
                        d.Id,
                        d.Domain,
                        d.IsVerified,
                        d.VerificationToken,
                        d.IsSpfVerified,
                        d.IsDkimVerified,
                        d.IsDmarcVerified,
                    })
                    .ToListAsync();

                // 2️⃣ Emails (can be multiple per domain)
                var emails = await _db.DomainEmailVerification
                    .Where(e => e.ClientId == clientId)
                    .Select(e => new
                    {
                        e.Id,
                        e.Email,
                        e.DomainId,
                        e.IsEmailVerified
                    })
                    .ToListAsync();

                // 3️⃣ Group emails by DomainId
                var emailLookup = emails
                    .GroupBy(e => e.DomainId)
                    .ToDictionary(g => g.Key, g => g.ToList());

                // 4️⃣ Map: 1 domain → 1 record
                var result = domains.Select(domain =>
                {
                    emailLookup.TryGetValue(domain.Id, out var domainEmails);

                    var anyEmailVerified = domainEmails?.Any(x => x.IsEmailVerified) ?? false;
                    var firstEmail = domainEmails?.FirstOrDefault();

                    return new DomainVeryficationStatus
                    {
                        Domainid = domain.Id,
                        Domain = domain.Domain,
                        Domainverified = domain.IsVerified,

                        EmailDomainId = firstEmail?.Id ?? 0,
                        token = $"pitchgen-verification={domain.VerificationToken}",

                        Dmark = GetDnsStatus(
                            domain.IsSpfVerified,
                            domain.IsDkimVerified,
                            domain.IsDmarcVerified
                        )
                    };
                }).ToList();

                // 5️⃣ Return all domains for this client
                return new OperationResult
                {
                    Success = true,
                    Message = "All domain verification statuses fetched successfully",
                    Data = result
                };
            }
            catch
            {
                return new OperationResult
                {
                    Success = false,
                    Message = "Failed to get domain verification status"
                };
            }
        }
        public async Task<List<string>> GetSpfRecords(string domain)
        {
            var lookup = new LookupClient();

            var result = await lookup.QueryAsync(domain, QueryType.TXT);

            return result.Answers
                .OfType<TxtRecord>()
                .SelectMany(x => x.Text)
                .Where(t => t.StartsWith("v=spf1", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        public bool IsSmtpAllowedInSpf(List<string> spfRecords, string smtpServer)
        {
            smtpServer = smtpServer.ToLower();

            foreach (var spf in spfRecords)
            {
                var parts = spf.Split(' ');

                foreach (var part in parts)
                {
                    if (part.StartsWith("include:"))
                    {
                        var includeDomain = part.Replace("include:", "");
                        if (smtpServer.EndsWith(includeDomain))
                            return true;
                    }
                }

                // basic support
                if (spf.Contains("mx") || spf.Contains("a"))
                    return true;
            }

            return false;
        }
        public async Task<bool> HasDkimRecord(string domain)
        {
            var lookup = new LookupClient();

            string[] selectors =
                    {
                "default",
                "selector1",
                "selector2",
                "mail",
                "dkim"
            };

            foreach (var selector in selectors)
            {
                var dkimDomain = $"{selector}._domainkey.{domain}";

                var result = await lookup.QueryAsync(dkimDomain, QueryType.TXT);

                if (result.Answers.OfType<TxtRecord>().Any())
                    return true;
            }

            return false;
        }
        public async Task<bool> HasDmarcRecord(string domain)
        {
            var lookup = new LookupClient();

            var dmarcDomain = $"_dmarc.{domain}";

            var result = await lookup.QueryAsync(dmarcDomain, QueryType.TXT);

            return result.Answers
                .OfType<TxtRecord>()
                .SelectMany(x => x.Text)
                .Any(t => t.StartsWith("v=DMARC1", StringComparison.OrdinalIgnoreCase));
        }

        public async Task<OperationResult> VerifySmtpOtp(string email, string otp, string clientId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(otp))
                    return Fail("Email or OTP is invalid");

                if (!int.TryParse(clientId, out int userId))
                    return Fail("Invalid clientId");

                var otpEntry = await _db.EmailOtpVerifications
                    .Where(x =>
                        x.Email == email &&
                        x.OTP == otp &&
                        !x.IsVerified &&
                        x.ExpiresAt > DateTime.UtcNow &&
                        x.OtpType == "DomainVerify")
                    .OrderByDescending(x => x.CreatedAt)
                    .FirstOrDefaultAsync();

                if (otpEntry == null)
                    return Fail("Invalid or expired OTP");

                if (string.IsNullOrWhiteSpace(otpEntry.TempSmtpPayload))
                    return Fail("SMTP payload not found");

                // ✅ Mark OTP verified
                otpEntry.IsVerified = true;

                // 🔹 Deserialize SMTP
                var smtpDto = JsonSerializer.Deserialize<SmtpCredentialDto>(
                    otpEntry.TempSmtpPayload);

                if (smtpDto == null)
                    return Fail("Invalid SMTP payload");

                // 🔹 Save SMTP ONLY AFTER OTP VERIFY
                var smtpEntity = new SmtpCredentials
                {
                    ClientId = clientId,
                    Server = smtpDto.Server,
                    Port = smtpDto.Port,
                    Username = smtpDto.Username,
                    Password = smtpDto.Password,
                    FromEmail = smtpDto.FromEmail,
                    SenderName = smtpDto.SenderName,
                    UseSsl = smtpDto.UseSsl,
                    DomainId = smtpDto.DomainId,
                    SecurityType = smtpDto.SecurityType,
                };
                
                var emailRecord = new DomainEmailVerification
                {
                    ClientId = userId,
                    DomainId = smtpDto.DomainId,
                    Email = smtpDto.FromEmail,
                    IsEmailVerified = true,
                    CreatedAt = DateTime.UtcNow,
                    EmailVerifiedAt = DateTime.UtcNow
                };

                await _db.DomainEmailVerification.AddAsync(emailRecord);
                await _db.SmtpCredentials.AddAsync(smtpEntity);
                await _db.SaveChangesAsync();

                return Success("SMTP configuration verified and saved successfully");
            }
            catch (Exception ex)
            {
                // log ex if needed
                return Fail("Failed to verify SMTP OTP");
            }
        }

        public async Task<bool> CustomDKIM(string domain, string selector, string expectedDkimValue, int clientId)
        {

            if (string.IsNullOrWhiteSpace(domain) ||
                string.IsNullOrWhiteSpace(selector) ||
                string.IsNullOrWhiteSpace(expectedDkimValue))
                return false;

            var verification = await _db.DomainVerification
                .FirstOrDefaultAsync(x => x.Domain == domain && x.ClientId == clientId);

            if (verification == null)
                return false;

            var lookup = new LookupClient();

            // selector._domainkey.domain.com
            var dkimDomain = $"{selector}.{domain}";

            var result = await lookup.QueryAsync(dkimDomain, QueryType.TXT);

            bool dkimOk = result.Answers
                .OfType<TxtRecord>()
                .Any(txt =>
                    txt.Text.Any(t =>
                        t.Contains(expectedDkimValue, StringComparison.OrdinalIgnoreCase)
                    )
                );

            verification.IsDkimVerified = dkimOk;
            await _db.SaveChangesAsync();

            return dkimOk;
        }


        public async Task<bool> CustomDMARC(string domain, string dmarcPrefix, string expectedDmarcValue, int clientId)
        {

            if (string.IsNullOrWhiteSpace(domain) ||
                string.IsNullOrWhiteSpace(dmarcPrefix) ||
                string.IsNullOrWhiteSpace(expectedDmarcValue))
                return false;

            var verification = await _db.DomainVerification
                .FirstOrDefaultAsync(x => x.Domain == domain && x.ClientId == clientId);

            if (verification == null)
                return false;

            var lookup = new LookupClient();

            // example: _dmarc.groupji.co
            var dmarcDomain = $"{dmarcPrefix}.{domain}";

            var result = await lookup.QueryAsync(dmarcDomain, QueryType.TXT);

            bool dmarcOk = result.Answers
                .OfType<TxtRecord>()
                .Any(txt =>
                    txt.Text.Any(t =>
                        t.StartsWith("v=DMARC1", StringComparison.OrdinalIgnoreCase) &&
                        t.Contains(expectedDmarcValue, StringComparison.OrdinalIgnoreCase)
                    )
                );

            verification.IsDmarcVerified = dmarcOk;
            await _db.SaveChangesAsync();

            return dmarcOk;
        }


        //public async Task<bool> IsSmtpFullyVerifiedAsync(int smtpId)
        //{
        //    // 1. Get SMTP credentials
        //    var smtp = await _db.SmtpCredentials
        //        .FirstOrDefaultAsync(x => x.Id == smtpId);

        //    if (smtp == null || string.IsNullOrWhiteSpace(smtp.FromEmail))
        //        return false;

        //    // 2. Extract domain from FromEmail
        //    var domain = GetDomain(smtp.FromEmail);
        //    int userId = int.TryParse(smtp.ClientId, out var id) ? id : 0;

        //    if (string.IsNullOrWhiteSpace(domain))
        //        return false;

        //    // 3. Check domain verification (SPF / DKIM / DMARC)
        //    var domainVerification = await _db.DomainVerification
        //        .FirstOrDefaultAsync(x =>
        //            x.Domain == domain &&
        //            x.ClientId == userId);

        //    if (domainVerification == null)
        //        return false;

        //    if (!domainVerification.IsSpfVerified ||
        //        !domainVerification.IsDkimVerified ||
        //        !domainVerification.IsDmarcVerified ||
        //        !domainVerification.IsVerified)
        //    {
        //        return false;
        //    }

        //    // 4. Check FromEmail verification
        //    var emailVerification = await _db.DomainEmailVerification
        //        .FirstOrDefaultAsync(x =>
        //            x.Email == smtp.FromEmail &&
        //            x.ClientId == userId);

        //    if (emailVerification == null || !emailVerification.IsEmailVerified)
        //        return false;

        //    // ✅ Everything verified
        //    return true;
        //}

        public async Task<OperationResult> DeleteDomainAsync(int domainId, string clientId)
        {
            using var transaction = await _db.Database.BeginTransactionAsync();

            try
            {
                if (!int.TryParse(clientId, out int userId))
                    return Fail("Invalid clientId");
                // 1️⃣ SMTP Credentials
                var smtpRecords = await _db.SmtpCredentials
                    .Where(x => x.DomainId == domainId && x.ClientId == clientId)
                    .ToListAsync();

                if (smtpRecords.Any())
                    _db.SmtpCredentials.RemoveRange(smtpRecords);

                // 2️⃣ Domain Email Verification
                var emailRecords = await _db.DomainEmailVerification
                    .Where(x => x.DomainId == domainId && x.ClientId == userId)
                    .ToListAsync();

                if (emailRecords.Any())
                    _db.DomainEmailVerification.RemoveRange(emailRecords);

                // 3️⃣ Domain Verification
                var domain = await _db.DomainVerification
                    .FirstOrDefaultAsync(x => x.Id == domainId && x.ClientId == userId);

                if (domain == null)
                {
                    await transaction.RollbackAsync();
                    return Fail("Domain not found");
                }

                _db.DomainVerification.Remove(domain);

                await _db.SaveChangesAsync();
                await transaction.CommitAsync();

                return Success("Domain deleted successfully");
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                return Fail(ex.Message);
            }
        }


        // ================================
        // DNS TXT Check
        // ================================
        private static async Task<bool> CheckTxtRecord(string host, string expectedValue)
        {
            try
            {
                var lookup = new LookupClient();
                var result = await lookup.QueryAsync(host, QueryType.TXT);

                foreach (var txt in result.Answers.TxtRecords())
                {
                    var value = string.Join("", txt.Text);
                    if (value.Contains(expectedValue, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch { }

            return false;
        }

        // ================================
        // Helpers
        // ================================
        private string GetDomain(string email)
        {
            if (string.IsNullOrWhiteSpace(email))
                return null;

            email = email.Trim();

            int atIndex = email.LastIndexOf('@');

            if (atIndex <= 0 || atIndex == email.Length - 1)
                return null;

            return email.Substring(atIndex + 1).ToLowerInvariant();
        }

        private string GetDnsStatus(bool isSpf, bool isDkim, bool isDmarc)
        {
            if (!isSpf && !isDkim && !isDmarc)
                return "No SPF, DKIM, DMARC";

            if (!isSpf && !isDkim)
                return "No SPF, DKIM";

            if (!isSpf && !isDmarc)
                return "No SPF, DMARC";

            if (!isDkim && !isDmarc)
                return "No DKIM, DMARC";

            if (!isSpf)
                return "No SPF";

            if (!isDkim)
                return "No DKIM";

            if (!isDmarc)
                return "No DMARC";

            return "All records verified";
        }

        private static string GenerateTokenValue()
        {
            return Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLower();
        }

        private OperationResult Success(string message, bool alreadyVerified = false)
        {
            return new OperationResult
            {
                Success = true,
                Message = message,
                DomainAlreadyVerified = alreadyVerified
            };
        }

        private OperationResult Fail(string message)
        {
            return new OperationResult
            {
                Success = false,
                Message = message
            };
        }
    }
}
