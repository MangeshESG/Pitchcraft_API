namespace PitchGenApi.Interfaces
{
    /// <summary>
    /// Reads/writes the admin-controlled model for each AI purpose.
    /// Every API that calls a model goes through here instead of hardcoding one.
    /// </summary>
    public interface IAiModelSettingsService
    {
        /// <summary>
        /// Every purpose mapped to its effective model (stored value, or the
        /// compiled-in default when nothing is stored).
        /// </summary>
        Task<Dictionary<string, string>> GetAllAsync();

        /// <summary>Effective model for one purpose. Never returns empty.</summary>
        Task<string> GetModelAsync(string purposeKey);

        /// <summary>
        /// Upserts the supplied purposes and returns the full effective map.
        /// Unknown keys are ignored; blank values reset a purpose to its default.
        /// </summary>
        Task<Dictionary<string, string>> SaveAsync(
            IDictionary<string, string?> values,
            string? updatedBy);
    }
}
