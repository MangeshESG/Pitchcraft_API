namespace PitchGenApi.Services
{
    using Microsoft.EntityFrameworkCore;
    using PitchGenApi.Database;
    using PitchGenApi.Interfaces;
    using PitchGenApi.Model;

    /// <summary>
    /// Admin-editable AI instructions backed by the app_prompt_settings table.
    /// The table is the only source: a key with no row is an empty instruction,
    /// not a compiled-in default.
    ///
    /// Same shape as <see cref="AiModelSettingsService"/>: the text is read on
    /// every request that runs the prompt but changes only when an admin saves
    /// the page, so the table is held in a process-wide snapshot with a short
    /// TTL and the snapshot is dropped on save.
    /// </summary>
    public class PromptSettingsService : IPromptSettingsService
    {
        private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(2);
        private static readonly SemaphoreSlim CacheLock = new(1, 1);

        private static Dictionary<string, string>? _cache;
        private static DateTime _cacheLoadedAtUtc;

        private readonly AppDbContext _context;
        private readonly ILogger<PromptSettingsService> _logger;

        public PromptSettingsService(
            AppDbContext context,
            ILogger<PromptSettingsService> logger)
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

                var effective = BuildEmpty();

                try
                {
                    var stored = await _context.app_prompt_settings
                        .AsNoTracking()
                        .ToListAsync();

                    foreach (var row in stored)
                    {
                        if (!PromptKeys.IsKnown(row.prompt_key)) continue;
                        if (string.IsNullOrWhiteSpace(row.prompt_text)) continue;

                        effective[PromptKeys.Normalize(row.prompt_key)] = row.prompt_text;
                    }
                }
                catch (Exception ex)
                {
                    // A missing table (script not run yet) leaves every prompt
                    // empty, which the callers report as "not configured".
                    _logger.LogWarning(
                        ex,
                        "Could not read app_prompt_settings; every prompt reads as empty.");
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

        /// <summary>
        /// The stored text, or "" when nothing is saved for this key. Callers
        /// are expected to check for empty and stop rather than send a blank
        /// instruction to a model.
        /// </summary>
        public async Task<string> GetPromptAsync(string promptKey)
        {
            var all = await GetAllAsync();

            return all.TryGetValue(promptKey, out var text) && !string.IsNullOrWhiteSpace(text)
                ? text
                : "";
        }

        public async Task<Dictionary<string, string>> SaveAsync(
            IDictionary<string, string?> values,
            string? updatedBy)
        {
            var rows = await _context.app_prompt_settings.ToListAsync();

            foreach (var pair in values)
            {
                if (!PromptKeys.IsKnown(pair.Key)) continue;

                var promptKey = PromptKeys.Normalize(pair.Key);
                var promptText = pair.Value;
                var existing = rows.FirstOrDefault(
                    row => string.Equals(row.prompt_key, promptKey, StringComparison.OrdinalIgnoreCase));

                // Blank means "no instruction at all" — drop the row.
                if (string.IsNullOrWhiteSpace(promptText))
                {
                    if (existing != null) _context.app_prompt_settings.Remove(existing);
                    continue;
                }

                if (existing == null)
                {
                    _context.app_prompt_settings.Add(new PromptSetting
                    {
                        prompt_key = promptKey,
                        prompt_text = promptText,
                        updated_at = DateTime.UtcNow,
                        updated_by = updatedBy
                    });
                }
                else
                {
                    existing.prompt_text = promptText;
                    existing.updated_at = DateTime.UtcNow;
                    existing.updated_by = updatedBy;
                }
            }

            await _context.SaveChangesAsync();
            InvalidateCache();

            return await GetAllAsync();
        }

        public async Task<Dictionary<string, (DateTime UpdatedAt, string? UpdatedBy)>> GetMetadataAsync()
        {
            var metadata = new Dictionary<string, (DateTime, string?)>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var stored = await _context.app_prompt_settings
                    .AsNoTracking()
                    .ToListAsync();

                foreach (var row in stored)
                {
                    if (!PromptKeys.IsKnown(row.prompt_key)) continue;

                    metadata[PromptKeys.Normalize(row.prompt_key)] = (row.updated_at, row.updated_by);
                }
            }
            catch (Exception ex)
            {
                // Same reasoning as GetAllAsync: without the table the page
                // simply shows every prompt as "running on the default".
                _logger.LogWarning(
                    ex,
                    "Could not read app_prompt_settings metadata.");
            }

            return metadata;
        }

        /// <summary>Every known key mapped to "", before the stored rows land on top.</summary>
        private static Dictionary<string, string> BuildEmpty()
        {
            var empty = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var promptKey in PromptKeys.All)
            {
                empty[promptKey] = "";
            }

            return empty;
        }

        private static void InvalidateCache()
        {
            _cache = null;
            _cacheLoadedAtUtc = DateTime.MinValue;
        }
    }
}
