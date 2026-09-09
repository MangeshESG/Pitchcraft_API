namespace PitchGenApi.Services
{
    using Microsoft.EntityFrameworkCore;
    using PitchGenApi.Database;
    using PitchGenApi.Interfaces;
    using PitchGenApi.Model;

    /// <summary>
    /// The Audience Assurance tuning values, backed by the app-wide key/value
    /// settings table (<c>app_security_settings</c>).
    ///
    /// The table is shared rather than one of its own because the API applies
    /// no migrations at startup — a new table would have to be created by hand
    /// on every environment before the admin page could save anything, which
    /// is a poor trade for a single integer. The rows are keyed, so nothing
    /// here can collide with the security switches that live alongside them.
    ///
    /// Deliberately uncached, unlike the security switches. The batch size is
    /// read once when a run starts, not on every request, so a query costs
    /// nothing measurable — and the admin page and the runner are not
    /// guaranteed to be the same process, so a cache here would mean saving a
    /// new number and watching the next run ignore it.
    /// </summary>
    public class ValidationSettingsService : IValidationSettingsService
    {
        private readonly AppDbContext _context;
        private readonly IConfiguration _configuration;
        private readonly ILogger<ValidationSettingsService> _logger;

        public ValidationSettingsService(
            AppDbContext context,
            IConfiguration configuration,
            ILogger<ValidationSettingsService> logger)
        {
            _context = context;
            _configuration = configuration;
            _logger = logger;
        }

        public async Task<int> GetBatchSizeAsync(
            CancellationToken cancellationToken = default)
        {
            try
            {
                var stored = await _context.app_security_settings
                    .AsNoTracking()
                    .Where(row => row.setting_key == ValidationSettingKeys.BatchSize)
                    .Select(row => row.setting_value)
                    .FirstOrDefaultAsync(cancellationToken);

                if (int.TryParse(stored, out var parsed))
                {
                    return ValidationSettingKeys.NormaliseBatchSize(parsed);
                }
            }
            catch (Exception ex)
            {
                // A missing table or an unreachable database must not fail a
                // run that is otherwise ready to go: fall through to the
                // configured value.
                _logger.LogWarning(
                    ex,
                    "Could not read the validation batch size; falling back to configuration.");
            }

            return ValidationSettingKeys.NormaliseBatchSize(
                _configuration.GetValue<int?>("Validation:BatchSize"));
        }

        public async Task<int> SetBatchSizeAsync(int batchSize, string? updatedBy)
        {
            // Clamped rather than rejected: the caller is a slider-style admin
            // field, and a stored value out of range would be silently ignored
            // by every run afterwards.
            var normalised = Math.Clamp(
                batchSize,
                ValidationSettingKeys.MinBatchSize,
                ValidationSettingKeys.MaxBatchSize);

            var key = ValidationSettingKeys.BatchSize;

            var existing = await _context.app_security_settings
                .FirstOrDefaultAsync(row => row.setting_key == key);

            if (existing == null)
            {
                _context.app_security_settings.Add(new SecuritySetting
                {
                    setting_key = key,
                    setting_value = normalised.ToString(),
                    updated_at = DateTime.UtcNow,
                    updated_by = updatedBy
                });
            }
            else
            {
                existing.setting_value = normalised.ToString();
                existing.updated_at = DateTime.UtcNow;
                existing.updated_by = updatedBy;
            }

            await _context.SaveChangesAsync();

            return normalised;
        }

        public async Task<(DateTime UpdatedAt, string? UpdatedBy)?> GetBatchSizeMetadataAsync()
        {
            try
            {
                var row = await _context.app_security_settings
                    .AsNoTracking()
                    .Where(r => r.setting_key == ValidationSettingKeys.BatchSize)
                    .Select(r => new { r.updated_at, r.updated_by })
                    .FirstOrDefaultAsync();

                return row == null ? null : (row.updated_at, row.updated_by);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Could not read the validation batch size metadata.");
                return null;
            }
        }
    }
}
