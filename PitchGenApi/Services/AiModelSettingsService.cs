namespace PitchGenApi.Services
{
    using Microsoft.EntityFrameworkCore;
    using PitchGenApi.Database;
    using PitchGenApi.Interfaces;
    using PitchGenApi.Model;

    /// <summary>
    /// Model-per-purpose settings backed by the ai_model_settings table.
    ///
    /// The values are read on nearly every generation request but change only
    /// when an admin saves the page, so the table is cached in a process-wide
    /// snapshot with a short TTL and the cache is dropped on save.
    /// </summary>
    public class AiModelSettingsService : IAiModelSettingsService
    {
        private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(2);
        private static readonly SemaphoreSlim CacheLock = new(1, 1);

        private static Dictionary<string, string>? _cache;
        private static DateTime _cacheLoadedAtUtc;

        private readonly AppDbContext _context;
        private readonly ILogger<AiModelSettingsService> _logger;

        public AiModelSettingsService(
            AppDbContext context,
            ILogger<AiModelSettingsService> logger)
        {
            _context = context;
            _logger = logger;
        }

        public async Task<Dictionary<string, string>> GetAllAsync()
        {
            var cached = _cache;
            if (cached != null && DateTime.UtcNow - _cacheLoadedAtUtc < CacheTtl)
            {
                return new Dictionary<string, string>(cached, StringComparer.OrdinalIgnoreCase);
            }

            await CacheLock.WaitAsync();
            try
            {
                cached = _cache;
                if (cached != null && DateTime.UtcNow - _cacheLoadedAtUtc < CacheTtl)
                {
                    return new Dictionary<string, string>(cached, StringComparer.OrdinalIgnoreCase);
                }

                var effective = BuildDefaults();

                try
                {
                    var stored = await _context.ai_model_settings
                        .AsNoTracking()
                        .ToListAsync();

                    foreach (var row in stored)
                    {
                        if (!AiModelPurposes.IsKnown(row.purpose_key)) continue;
                        if (string.IsNullOrWhiteSpace(row.model_name)) continue;

                        effective[row.purpose_key] = row.model_name.Trim();
                    }
                }
                catch (Exception ex)
                {
                    // A missing table (script not run yet) must not take generation
                    // down — fall back to the compiled-in defaults.
                    _logger.LogWarning(
                        ex,
                        "Could not read ai_model_settings; using default AI models.");
                }

                _cache = new Dictionary<string, string>(effective, StringComparer.OrdinalIgnoreCase);
                _cacheLoadedAtUtc = DateTime.UtcNow;

                return effective;
            }
            finally
            {
                CacheLock.Release();
            }
        }

        public async Task<string> GetModelAsync(string purposeKey)
        {
            var all = await GetAllAsync();

            return all.TryGetValue(purposeKey, out var model) && !string.IsNullOrWhiteSpace(model)
                ? model
                : AiModelDefaults.ForPurpose(purposeKey);
        }

        public async Task<Dictionary<string, string>> SaveAsync(
            IDictionary<string, string?> values,
            string? updatedBy)
        {
            var rows = await _context.ai_model_settings.ToListAsync();

            foreach (var pair in values)
            {
                if (!AiModelPurposes.IsKnown(pair.Key)) continue;

                var purposeKey = AiModelPurposes.All.First(
                    key => string.Equals(key, pair.Key, StringComparison.OrdinalIgnoreCase));

                // Blank means "go back to the built-in default" — drop the row.
                var modelName = pair.Value?.Trim();
                var existing = rows.FirstOrDefault(
                    row => string.Equals(row.purpose_key, purposeKey, StringComparison.OrdinalIgnoreCase));

                if (string.IsNullOrWhiteSpace(modelName))
                {
                    if (existing != null) _context.ai_model_settings.Remove(existing);
                    continue;
                }

                if (existing == null)
                {
                    _context.ai_model_settings.Add(new AiModelSetting
                    {
                        purpose_key = purposeKey,
                        model_name = modelName,
                        updated_at = DateTime.UtcNow,
                        updated_by = updatedBy
                    });
                }
                else
                {
                    existing.model_name = modelName;
                    existing.updated_at = DateTime.UtcNow;
                    existing.updated_by = updatedBy;
                }
            }

            await _context.SaveChangesAsync();
            InvalidateCache();

            return await GetAllAsync();
        }

        private static Dictionary<string, string> BuildDefaults()
        {
            var defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var purpose in AiModelPurposes.All)
            {
                defaults[purpose] = AiModelDefaults.ForPurpose(purpose);
            }

            return defaults;
        }

        private static void InvalidateCache()
        {
            _cache = null;
            _cacheLoadedAtUtc = DateTime.MinValue;
        }
    }
}
