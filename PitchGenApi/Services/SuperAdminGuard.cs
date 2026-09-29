namespace PitchGenApi.Services
{
    using System.Security.Claims;
    using Microsoft.EntityFrameworkCore;
    using PitchGenApi.Database;
    using PitchGenApi.Interfaces;

    /// <summary>
    /// Configuration-backed implementation of <see cref="ISuperAdminGuard"/>.
    ///
    /// The allowlist lives in appsettings under <c>SuperAdmins:ClientIds</c>
    /// rather than in a database column so that who can mint accounts and
    /// credits differs per environment and cannot be changed from inside the
    /// product — an admin who somehow got write access to the clients table
    /// still could not add themselves to it.
    ///
    /// Fails closed. An empty or missing list means nobody is allowed, which
    /// is the safe way round for a list this powerful: a misdeployed config
    /// turns the panels off rather than opening them to every admin.
    /// </summary>
    public class SuperAdminGuard : ISuperAdminGuard
    {
        private const string AllowlistPath = "SuperAdmins:ClientIds";

        private readonly AppDbContext _context;
        private readonly IConfiguration _configuration;
        private readonly ILogger<SuperAdminGuard> _logger;

        public SuperAdminGuard(
            AppDbContext context,
            IConfiguration configuration,
            ILogger<SuperAdminGuard> logger)
        {
            _context = context;
            _configuration = configuration;
            _logger = logger;
        }

        public async Task<SuperAdminCheck> AuthorizeAsync(
            ClaimsPrincipal user,
            CancellationToken cancellationToken = default)
        {
            // Same claim the impersonation endpoint reads, so one signed token
            // identifies the caller everywhere.
            if (!int.TryParse(user.FindFirst("UserId")?.Value, out var callerId) ||
                callerId <= 0)
            {
                return new SuperAdminCheck(
                    false, 0, 401, "Sign in again and retry.");
            }

            var allowed = ReadAllowlist();

            if (allowed.Count == 0)
            {
                _logger.LogWarning(
                    "Super-admin action refused for client {CallerId}: no ids are " +
                    "configured under {AllowlistPath}.",
                    callerId,
                    AllowlistPath);

                return new SuperAdminCheck(
                    false, callerId, 403,
                    "No accounts are authorised for this action.");
            }

            if (!allowed.Contains(callerId))
            {
                _logger.LogWarning(
                    "Super-admin action refused for client {CallerId}: not on the " +
                    "configured allowlist.",
                    callerId);

                return new SuperAdminCheck(
                    false, callerId, 403,
                    "Your account is not authorised for this action.");
            }

            // The allowlist is checked against a live account, so removing an
            // admin's IsAdmin flag revokes this too without a config change.
            var caller = await _context.ClientDetails
                .AsNoTracking()
                .FirstOrDefaultAsync(client => client.Id == callerId, cancellationToken);

            if (caller == null || !caller.IsAdmin)
            {
                _logger.LogWarning(
                    "Super-admin action refused for client {CallerId}: account is " +
                    "missing or no longer an admin.",
                    callerId);

                return new SuperAdminCheck(
                    false, callerId, 403,
                    "Your account is not authorised for this action.");
            }

            return new SuperAdminCheck(true, callerId, 200, null);
        }

        /// <summary>
        /// Reads the allowlist. Values are accepted as numbers or strings so
        /// that <c>[1, 5]</c> and <c>["1", "5"]</c> both work — the JSON and
        /// the environment-variable form of the same setting.
        /// </summary>
        private HashSet<int> ReadAllowlist()
        {
            var ids = new HashSet<int>();

            foreach (var entry in _configuration.GetSection(AllowlistPath).GetChildren())
            {
                if (int.TryParse(entry.Value, out var id) && id > 0)
                {
                    ids.Add(id);
                }
            }

            return ids;
        }
    }
}
