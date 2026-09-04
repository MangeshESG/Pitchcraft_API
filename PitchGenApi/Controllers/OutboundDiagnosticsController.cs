using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace PitchGenApi.Controllers
{
    /// <summary>
    /// Temporary: answers "why does this server reach OpenAI but not DeepSeek?"
    /// from inside the app, on the box that actually makes the calls. It cannot
    /// be reproduced from a developer machine - that machine works. Deployment
    /// here is over FTP with no shell, so the check has to run over HTTP.
    /// Delete this controller once the DeepSeek route is settled.
    /// </summary>
    [ApiController]
    [Route("api/diagnostics")]
    public class OutboundDiagnosticsController : ControllerBase
    {
        private readonly IConfiguration _configuration;

        public OutboundDiagnosticsController(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        [HttpGet("outbound")]
        public async Task<IActionResult> Outbound([FromQuery] string? token)
        {
            var expected = _configuration["DiagnosticsToken"];

            // No token configured means the endpoint stays shut rather than
            // becoming an open port scanner for anyone who finds the URL.
            if (string.IsNullOrWhiteSpace(expected) || token != expected)
            {
                return NotFound();
            }

            // DeepSeek is tried under each protocol setting separately. If only
            // one combination fails, the cause is protocol negotiation; if all
            // fail identically, the protocol is irrelevant and the problem is
            // the certificate chain or something cutting the connection.
            // OpenAI is the control: it succeeds from this server today, so any
            // difference between the two is DeepSeek-specific.
            var probes = new (string Host, string Label, SslProtocols Protocols)[]
            {
                ("api.deepseek.com", "tls12+tls13", SslProtocols.Tls12 | SslProtocols.Tls13),
                ("api.deepseek.com", "tls12-only", SslProtocols.Tls12),
                ("api.deepseek.com", "tls13-only", SslProtocols.Tls13),
                ("api.deepseek.com", "os-default", SslProtocols.None),
                ("api.openai.com", "tls12+tls13 (control)", SslProtocols.Tls12 | SslProtocols.Tls13)
            };

            var results = new List<object>();

            foreach (var probe in probes)
            {
                results.Add(await ProbeAsync(probe.Host, probe.Label, probe.Protocols));
            }

            return Ok(new
            {
                // Kept even though the clock is no longer the prime suspect: it
                // is cheap to rule out and expensive to overlook.
                serverTimeUtc = DateTime.UtcNow,
                serverTimeLocal = DateTime.Now,
                serverTimeZone = TimeZoneInfo.Local.DisplayName,
                osVersion = Environment.OSVersion.ToString(),
                machine = Environment.MachineName,
                probes = results
            });
        }

        /// <summary>
        /// Walks the connection one layer at a time - DNS, TCP, then the TLS
        /// handshake - so the answer says which layer failed. The decisive field
        /// is certificateSeen: a handshake that fails without the far end ever
        /// presenting a certificate is being cut before the certificate
        /// exchange (filtering), not rejected on its contents (a chain fault).
        /// </summary>
        private static async Task<object> ProbeAsync(
            string host,
            string label,
            SslProtocols protocols)
        {
            var stopwatch = Stopwatch.StartNew();
            string[] addresses;

            try
            {
                var resolved = await Dns.GetHostAddressesAsync(host);
                addresses = resolved.Select(a => a.ToString()).ToArray();
            }
            catch (Exception ex)
            {
                return new
                {
                    host,
                    label,
                    stage = "dns",
                    ok = false,
                    error = Describe(ex),
                    elapsedMs = stopwatch.ElapsedMilliseconds
                };
            }

            using var client = new TcpClient();

            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await client.ConnectAsync(host, 443, timeout.Token);
            }
            catch (Exception ex)
            {
                return new
                {
                    host,
                    label,
                    stage = "tcp",
                    ok = false,
                    addresses,
                    error = Describe(ex),
                    elapsedMs = stopwatch.ElapsedMilliseconds
                };
            }

            var tcpMs = stopwatch.ElapsedMilliseconds;
            X509Certificate2? presented = null;
            object[] chainDetail = Array.Empty<object>();
            string[] chainProblems = Array.Empty<string>();
            string? policyErrors = null;

            try
            {
                using var ssl = new SslStream(
                    client.GetStream(),
                    leaveInnerStreamOpen: false,
                    userCertificateValidationCallback: (_, certificate, chain, errors) =>
                    {
                        policyErrors = errors.ToString();

                        if (certificate is not null)
                        {
                            presented = new X509Certificate2(certificate);
                        }

                        // An expired link is rarely the leaf - it is a root or
                        // intermediate in the machine certificate store, so
                        // every link reports its dates, not just the first.
                        if (chain is not null)
                        {
                            chainDetail = chain.ChainElements
                                .Cast<X509ChainElement>()
                                .Select(element => (object)new
                                {
                                    subject = element.Certificate.Subject,
                                    issuer = element.Certificate.Issuer,
                                    notBefore = element.Certificate.NotBefore,
                                    notAfter = element.Certificate.NotAfter,
                                    expired = element.Certificate.NotAfter < DateTime.Now,
                                    status = element.ChainElementStatus
                                        .Select(st => st.Status.ToString())
                                        .ToArray()
                                })
                                .ToArray();

                            chainProblems = chain.ChainStatus
                                .Select(st => $"{st.Status}: {st.StatusInformation?.Trim()}")
                                .ToArray();
                        }

                        // Reports rather than enforces - the point is to see
                        // what is there. The real client still validates.
                        return true;
                    });

                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = host,
                    EnabledSslProtocols = protocols
                });

                return new
                {
                    host,
                    label,
                    stage = "tls",
                    ok = true,
                    addresses,
                    tcpMs,
                    elapsedMs = stopwatch.ElapsedMilliseconds,
                    protocol = ssl.SslProtocol.ToString(),
                    cipher = ssl.NegotiatedCipherSuite.ToString(),
                    certificateSeen = presented is not null,
                    policyErrors,
                    certificateSubject = presented?.Subject,
                    certificateIssuer = presented?.Issuer,
                    chain = chainDetail,
                    chainProblems
                };
            }
            catch (Exception ex)
            {
                return new
                {
                    host,
                    label,
                    stage = "tls",
                    ok = false,
                    addresses,
                    tcpMs,
                    elapsedMs = stopwatch.ElapsedMilliseconds,
                    error = Describe(ex),
                    certificateSeen = presented is not null,
                    policyErrors,
                    certificateSubject = presented?.Subject,
                    certificateIssuer = presented?.Issuer,
                    chain = chainDetail,
                    chainProblems
                };
            }
        }

        private static string Describe(Exception ex)
        {
            var parts = new List<string>();

            for (Exception? current = ex; current is not null; current = current.InnerException)
            {
                var detail = current switch
                {
                    SocketException socket => $" (SocketError.{socket.SocketErrorCode})",
                    System.ComponentModel.Win32Exception win32 => $" (0x{win32.NativeErrorCode:X8})",
                    _ => string.Empty
                };

                parts.Add($"{current.GetType().Name}: {current.Message}{detail}");
            }

            return string.Join(" -> ", parts);
        }
    }
}
