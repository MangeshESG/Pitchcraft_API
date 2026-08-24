namespace PitchGenApi.Services
{
    using Microsoft.EntityFrameworkCore;
    using PitchGenApi.Database;
    using PitchGenApi.Interfaces;
    using PitchGenApi.Model;

    /// <summary>
    /// Security switches backed by the app_security_settings table.
    ///
    /// The login OTP switch is read on every sign-in but changes only when an
    /// admin saves the page, so the table is cached in a process-wide snapshot
    /// with a short TTL and the cache is dropped on save.
    /// </summary>
    public class SecuritySettingsService : ISecuritySettingsService
    {
        private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(2);
        private static readonly SemaphoreSlim CacheLock = new(1, 1);

        private static Dictionary<string, string>? _cache;
        private static DateTime _cacheLoadedAtUtc;

        private readonly AppDbContext _context;
        private readonly ILogger<SecuritySettingsService> _logger;

        public SecuritySettingsService(
            AppDbContext context,
            ILogger<SecuritySettingsService> logger)
        {
            _context = context;
            _logger = logger;
        }

        public async Task<bool> IsLoginOtpEnabledAsync()
        {
            var all = await GetAllAsync();

            // No row stored means "never configured" — keep OTP on.
            return !all.TryGetValue(SecuritySettingKeys.LoginOtpEnabled, out var value) ||
                   !bool.TryParse(value, out var enabled) ||
                   enabled;
        }

        public async Task SetLoginOtpEnabledAsync(bool enabled, string? updatedBy)
        {
            var key = SecuritySettingKeys.LoginOtpEnabled;

            var existing = await _context.app_security_settings
                .FirstOrDefaultAsync(row => row.setting_key == key);

            if (existing == null)
            {
                _context.app_security_settings.Add(new SecuritySetting
                {
                    setting_key = key,
                    setting_value = enabled ? "true" : "false",
                    updated_at = DateTime.UtcNow,
                    updated_by = updatedBy
                });
            }
            else
            {
                existing.setting_value = enabled ? "true" : "false";
                existing.updated_at = DateTime.UtcNow;
                existing.updated_by = updatedBy;
            }

            await _context.SaveChangesAsync();
            InvalidateCache();
        }

        private async Task<Dictionary<string, string>> GetAllAsync()
        {
            var cached = _cache;
            if (cached != null && DateTime.UtcNow - _cacheLoadedAtUtc < CacheTtl)
            {
                return cached;
            }

            await CacheLock.WaitAsync();
            try
            {
                cached = _cache;
                if (cached != null && DateTime.UtcNow - _cacheLoadedAtUtc < CacheTtl)
                {
                    return cached;
                }

                var effective = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                try
                {
                    var stored = await _context.app_security_settings
                        .AsNoTracking()
                        .ToListAsync();

                    foreach (var row in stored)
                    {
                        if (!SecuritySettingKeys.IsKnown(row.setting_key)) continue;
                        if (string.IsNullOrWhiteSpace(row.setting_value)) continue;

                        effective[row.setting_key] = row.setting_value.Trim();
                    }
                }
                catch (Exception ex)
                {
                    // A missing table (script not run yet) must not take login
                    // down — an empty map means every switch keeps its default.
                    _logger.LogWarning(
                        ex,
                        "Could not read app_security_settings; using default security settings.");
                }

                _cache = effective;
                _cacheLoadedAtUtc = DateTime.UtcNow;

                return effective;
            }
            finally
            {
                CacheLock.Release();
            }
        }

        private static void InvalidateCache()
        {
            _cache = null;
            _cacheLoadedAtUtc = DateTime.MinValue;
        }
    }
}
