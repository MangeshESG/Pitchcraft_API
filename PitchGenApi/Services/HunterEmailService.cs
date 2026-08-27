namespace PitchGenApi.Services
{
    using System.Diagnostics;
    using System.Text.Json;
    using PitchGenApi.Interfaces;
    using PitchGenApi.Model.DTOs;

    /// <summary>
    /// Hunter.io Email Finder, used as the stage after the AI search.
    ///
    /// Hunter is asked for one address and answers with a 0-100 score, which is
    /// read on the same scale as the model's own confidence so the two can be
    /// compared directly by the caller. Only the Email Finder endpoint is
    /// called: one request per unlock, never a domain crawl.
    /// </summary>
    public class HunterEmailService : IHunterEmailService
    {
        private const string FinderEndpoint = "https://api.hunter.io/v2/email-finder";

        /// <summary>Used when Hunter:ConfidenceThreshold is absent or nonsensical.</summary>
        private const int DefaultConfidenceThreshold = 80;

        /// <summary>
        /// Hunter's own cap on how long it may spend, in seconds. The documented
        /// range is 3-20; 10 is its default and is kept here so a slow lookup
        /// cannot hold the unlock open indefinitely.
        /// </summary>
        private const int MaxDurationSeconds = 10;

        /// <summary>
        /// Hosts that are never an employer: mailbox providers, social networks,
        /// profile and directory sites, and the contact databases themselves.
        ///
        /// The extension falls back to a company's LinkedIn URL when it cannot
        /// scrape a website, and "linkedin.com" is a perfectly well-formed
        /// domain - so without this list every such contact was searched at
        /// linkedin.com, which of course has none of them.
        ///
        /// Subdomains are matched too, so "in.linkedin.com" is caught as well.
        /// </summary>
        private static readonly HashSet<string> NonCompanyDomains =
            new(StringComparer.OrdinalIgnoreCase)
            {
                // Mailbox providers
                "gmail.com", "googlemail.com", "yahoo.com", "yahoo.co.uk",
                "hotmail.com", "hotmail.co.uk", "outlook.com", "live.com",
                "msn.com", "aol.com", "icloud.com", "me.com", "mac.com",
                "protonmail.com", "proton.me", "gmx.com", "gmx.de",
                "mail.com", "yandex.com", "zoho.com", "qq.com", "163.com",

                // Social and profile sites
                "linkedin.com", "lnkd.in", "facebook.com", "fb.com",
                "twitter.com", "x.com", "instagram.com", "youtube.com",
                "tiktok.com", "threads.net", "github.com", "gitlab.com",
                "medium.com", "substack.com", "about.me", "linktr.ee",

                // Site builders and blog hosts
                "wordpress.com", "wixsite.com", "wix.com", "blogspot.com",
                "squarespace.com", "weebly.com", "godaddysites.com",
                "sites.google.com", "notion.site", "carrd.co",

                // Directories, job boards and contact databases
                "crunchbase.com", "bloomberg.com", "glassdoor.com",
                "indeed.com", "monster.com", "ziprecruiter.com",
                "wellfound.com", "angel.co", "zoominfo.com", "rocketreach.co",
                "apollo.io", "signalhire.com", "lusha.com", "hunter.io",
                "contactout.com", "seamless.ai", "leadiq.com", "uplead.com",
                "theorg.com", "owler.com", "dnb.com"
            };

        private readonly HttpClient _httpClient;
        private readonly IConfiguration _configuration;
        private readonly ILogger<HunterEmailService> _logger;

        public HunterEmailService(
            HttpClient httpClient,
            IConfiguration configuration,
            ILogger<HunterEmailService> logger)
        {
            _httpClient = httpClient;
            _configuration = configuration;
            _logger = logger;
        }

        private string? ApiKey
        {
            get
            {
                var key = _configuration["Hunter:ApiKey"];
                return string.IsNullOrWhiteSpace(key) ? null : key.Trim();
            }
        }

        public bool IsConfigured => ApiKey != null;

        public int ConfidenceThreshold
        {
            get
            {
                var configured = _configuration.GetValue<int?>("Hunter:ConfidenceThreshold");

                // Outside 1-100 the setting cannot mean anything: 0 would never
                // escalate and above 100 would always escalate.
                return configured is > 0 and <= 100
                    ? configured.Value
                    : DefaultConfidenceThreshold;
            }
        }

        public async Task<HunterLookupResult> FindEmailAsync(
            HunterLookupRequest request,
            CancellationToken cancellationToken = default)
        {
            var apiKey = ApiKey;

            if (apiKey == null)
                return HunterLookupResult.Skipped("No Hunter:ApiKey is configured.");

            var result = new HunterLookupResult
            {
                ApiKeyConfigured = true,
                Endpoint = FinderEndpoint
            };

            var (domain, domainSource) = ResolveDomain(request);
            var company = Clean(request.Company);
            var fullName = Clean(request.FullName);

            // Hunter matches a person against an employer. Without a name, or
            // without anything naming the employer, there is nothing to ask.
            if (fullName == null)
            {
                result.RejectedBecause = "No contact name was available to search with.";
                return result;
            }

            if (domain == null && company == null)
            {
                result.RejectedBecause =
                    "No company domain or company name was available to search with. " +
                    "Nothing supplied pointed at a real company website.";
                return result;
            }

            result.Domain = domain;
            result.DomainSource = domain == null
                ? "no usable domain; searched by company name instead"
                : domainSource;

            var query = new List<KeyValuePair<string, string>>();

            if (domain != null)
                query.Add(new("domain", domain));
            else
                query.Add(new("company", company!));

            // first_name/last_name is the pair Hunter matches on best; full_name
            // is its documented fallback for names that will not split.
            var (firstName, lastName) = SplitName(fullName);

            if (firstName != null && lastName != null)
            {
                query.Add(new("first_name", firstName));
                query.Add(new("last_name", lastName));
            }
            else
            {
                query.Add(new("full_name", fullName));
            }

            query.Add(new("max_duration", MaxDurationSeconds.ToString()));

            // Kept without the key so the trace can be shown to an admin.
            result.RequestUrl = FinderEndpoint + "?" + string.Join(
                "&",
                query.Select(pair =>
                    Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));

            var url = result.RequestUrl + "&api_key=" + Uri.EscapeDataString(apiKey);
            var timer = Stopwatch.StartNew();

            try
            {
                using var httpRequest = new HttpRequestMessage(HttpMethod.Get, url);
                using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);

                result.HttpStatus = (int)response.StatusCode;

                // Read as text first so the payload survives into the trace even
                // when it turns out not to be the JSON we expect.
                var rawBody = await response.Content.ReadAsStringAsync(cancellationToken);
                result.RawResponse = rawBody;

                HunterEmailFinderResponseDto? parsed = null;

                try
                {
                    parsed = JsonSerializer.Deserialize<HunterEmailFinderResponseDto>(rawBody);
                }
                catch (JsonException ex)
                {
                    result.RejectedBecause = "Hunter returned unreadable JSON: " + ex.Message;
                }

                if (result.RejectedBecause == null && !response.IsSuccessStatusCode)
                {
                    var detail = parsed?.Errors?.FirstOrDefault()?.Details;

                    result.RejectedBecause =
                        $"Hunter replied {(int)response.StatusCode} {response.StatusCode}" +
                        (string.IsNullOrWhiteSpace(detail) ? "." : ": " + detail);
                }

                if (result.RejectedBecause == null)
                {
                    var data = parsed?.Data;
                    var email = data?.Email?.Trim();

                    result.Score = data?.Score ?? 0;
                    result.VerificationStatus = data?.Verification?.Status;
                    result.Position = data?.Position;
                    result.SourceCount = data?.Sources?.Count ?? 0;

                    if (!string.IsNullOrWhiteSpace(data?.Domain))
                        result.Domain = data.Domain;

                    result.RejectedBecause =
                        string.IsNullOrWhiteSpace(email)
                            ? "Hunter found no address for this person at " +
                              (domain ?? company) + "."
                        : !System.Net.Mail.MailAddress.TryCreate(email, out _)
                            ? "Hunter returned '" + email + "', which is not a valid address."
                        : null;

                    if (result.RejectedBecause == null)
                        result.Email = email;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                result.RejectedBecause = "The Hunter call timed out.";
            }
            catch (HttpRequestException ex)
            {
                result.RejectedBecause = "The Hunter call failed: " + ex.Message;
                _logger.LogWarning(ex, "Hunter email-finder call failed.");
            }
            finally
            {
                timer.Stop();
                result.ElapsedMs = (int)timer.ElapsedMilliseconds;
            }

            return result;
        }

        /// <summary>
        /// The domain to ask Hunter about, and which input it came from.
        ///
        /// What the AI search found comes first. It is the only input here
        /// produced by actually researching this person; the extension's values
        /// are scraped from the profile page and are frequently the company's
        /// LinkedIn URL rather than its website. Returns a null domain when none
        /// of the inputs yields a real company domain.
        /// </summary>
        private static (string? Domain, string? Source) ResolveDomain(HunterLookupRequest request)
        {
            var candidates = new (string? Value, string Source)[]
            {
                (request.AiWebsite, "the company website the AI search reported"),
                (DomainOfEmail(request.EmailHint), "the domain of the address the AI search found"),
                (request.Domain, "the domain the extension read from the page"),
                (request.CompanyUrl, "the company URL the extension read from the page")
            };

            foreach (var (value, source) in candidates)
            {
                var domain = NormaliseDomain(value);

                if (domain != null)
                    return (domain, source);
            }

            return (null, null);
        }

        private static string? DomainOfEmail(string? email)
        {
            var at = email?.LastIndexOf('@') ?? -1;

            return at >= 0 && at < email!.Length - 1
                ? email[(at + 1)..]
                : null;
        }

        /// <summary>
        /// Reduces anything domain-shaped - "https://www.acme.com/about",
        /// "ACME.com", "acme.com:443" - to "acme.com". Returns null for text
        /// that is not a domain at all, and for any host that cannot be an
        /// employer (see <see cref="NonCompanyDomains"/>).
        /// </summary>
        private static string? NormaliseDomain(string? value)
        {
            var text = Clean(value);

            if (text == null)
                return null;

            // Strip a scheme, then any path, query or fragment.
            var schemeEnd = text.IndexOf("//", StringComparison.Ordinal);
            if (schemeEnd >= 0)
                text = text[(schemeEnd + 2)..];

            foreach (var separator in new[] { '/', '?', '#' })
            {
                var at = text.IndexOf(separator);
                if (at >= 0)
                    text = text[..at];
            }

            // Credentials and ports are not part of the domain.
            var credentials = text.LastIndexOf('@');
            if (credentials >= 0)
                text = text[(credentials + 1)..];

            var port = text.IndexOf(':');
            if (port >= 0)
                text = text[..port];

            text = text.Trim().Trim('.').ToLowerInvariant();

            if (text.StartsWith("www.", StringComparison.Ordinal))
                text = text[4..];

            // A domain has a dot, a label after it, and no whitespace.
            var lastDot = text.LastIndexOf('.');

            if (text.Length == 0 ||
                text.Any(char.IsWhiteSpace) ||
                lastDot <= 0 ||
                lastDot >= text.Length - 1)
            {
                return null;
            }

            return IsNonCompanyDomain(text) ? null : text;
        }

        /// <summary>
        /// Whether a normalised domain is on the never-an-employer list, either
        /// exactly or as a subdomain of one ("in.linkedin.com").
        /// </summary>
        private static bool IsNonCompanyDomain(string domain)
        {
            if (NonCompanyDomains.Contains(domain))
                return true;

            return NonCompanyDomains.Any(blocked =>
                domain.EndsWith("." + blocked, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Splits a display name into the first and last name Hunter matches on.
        /// Middle names and initials are dropped; a single-word name returns
        /// (null, null) so the caller falls back to full_name.
        /// </summary>
        private static (string? First, string? Last) SplitName(string fullName)
        {
            var parts = fullName
                .Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                // Initials and honorific dots carry no matching value.
                .Where(part => part.Trim('.').Length > 1)
                .ToArray();

            return parts.Length >= 2
                ? (parts[0], parts[^1])
                : (null, null);
        }

        private static string? Clean(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
