namespace PitchGenApi.Services
{
    using System.Diagnostics;
    using System.Net.Http.Json;
    using System.Text.Json;
    using PitchGenApi.Interfaces;
    using PitchGenApi.Model.DTOs;

    /// <summary>
    /// Prospeo person enrichment, asked for one verified address per LinkedIn
    /// profile URL.
    ///
    /// only_verified_email is set on every request, so Prospeo either returns
    /// an address it has verified or returns nothing — there is no middle
    /// confidence to interpret, which is why the result here is a yes/no plus
    /// a reason rather than a score. Mobile enrichment is explicitly off: it
    /// costs extra and nothing in the product uses it.
    /// </summary>
    public class ProspeoEmailService : IProspeoEmailService
    {
        private const string EnrichEndpoint = "https://api.prospeo.io/enrich-person";

        private readonly HttpClient _httpClient;
        private readonly IConfiguration _configuration;
        private readonly ILogger<ProspeoEmailService> _logger;

        public ProspeoEmailService(
            HttpClient httpClient,
            IConfiguration configuration,
            ILogger<ProspeoEmailService> logger)
        {
            _httpClient = httpClient;
            _configuration = configuration;
            _logger = logger;
        }

        private string? ApiKey
        {
            get
            {
                var key = _configuration["Prospeo:ApiKey"];
                return string.IsNullOrWhiteSpace(key) ? null : key.Trim();
            }
        }

        public bool IsConfigured => ApiKey != null;

        public async Task<ProspeoLookupResult> FindEmailAsync(
            string linkedInUrl,
            CancellationToken cancellationToken = default)
        {
            var apiKey = ApiKey;

            if (apiKey == null)
            {
                var skipped = ProspeoLookupResult.Skipped("No Prospeo:ApiKey is configured.");
                skipped.Endpoint = EnrichEndpoint;
                return skipped;
            }

            var result = new ProspeoLookupResult
            {
                ApiKeyConfigured = true,
                Endpoint = EnrichEndpoint
            };

            if (string.IsNullOrWhiteSpace(linkedInUrl))
            {
                result.RejectedBecause = "No LinkedIn URL was available to look up.";
                return result;
            }

            var body = new
            {
                only_verified_email = true,
                enrich_mobile = false,
                data = new { linkedin_url = linkedInUrl.Trim() }
            };

            result.RequestBody = JsonSerializer.Serialize(
                body,
                new JsonSerializerOptions { WriteIndented = true });

            var timer = Stopwatch.StartNew();

            try
            {
                using var httpRequest = new HttpRequestMessage(HttpMethod.Post, EnrichEndpoint);
                httpRequest.Headers.Add("X-KEY", apiKey);
                httpRequest.Content = JsonContent.Create(body);

                using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
                result.HttpStatus = (int)response.StatusCode;

                // Read as text first so the payload survives into the trace even
                // when it turns out not to be the JSON we expect.
                var rawBody = await response.Content.ReadAsStringAsync(cancellationToken);
                result.RawResponse = rawBody;

                if (!response.IsSuccessStatusCode)
                {
                    result.RejectedBecause =
                        $"Prospeo replied {(int)response.StatusCode} {response.StatusCode}.";
                    return result;
                }

                ProspeoEnrichResponseDto? parsed = null;

                try
                {
                    parsed = JsonSerializer.Deserialize<ProspeoEnrichResponseDto>(rawBody);
                }
                catch (JsonException ex)
                {
                    result.RejectedBecause = "Prospeo returned unreadable JSON: " + ex.Message;
                }

                var emailResult = parsed?.Person?.Email;
                var email = emailResult?.Email?.Trim();

                result.Revealed = emailResult?.Revealed;
                result.EmailStatus = emailResult?.Status;

                if (result.RejectedBecause == null)
                {
                    result.RejectedBecause =
                        parsed?.Error == true
                            ? "Prospeo flagged the response as an error."
                        : emailResult == null
                            ? "Prospeo returned no email object for this profile."
                        : !emailResult.Revealed
                            ? "Prospeo did not reveal the address."
                        : !string.Equals(emailResult.Status, "VERIFIED", StringComparison.OrdinalIgnoreCase)
                            ? "Prospeo status was '" + emailResult.Status + "', not VERIFIED."
                        : string.IsNullOrWhiteSpace(email)
                            ? "Prospeo returned an empty address."
                        : null;
                }

                if (result.RejectedBecause == null)
                    result.Email = email;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                result.RejectedBecause = "The Prospeo call timed out.";
            }
            catch (HttpRequestException ex)
            {
                result.RejectedBecause = "The Prospeo call failed: " + ex.Message;
                _logger.LogWarning(ex, "Prospeo enrich-person call failed.");
            }
            finally
            {
                timer.Stop();
                result.ElapsedMs = (int)timer.ElapsedMilliseconds;
            }

            return result;
        }
    }
}
