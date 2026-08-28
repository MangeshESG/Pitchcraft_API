namespace PitchGenApi.Interfaces
{
    /// <summary>
    /// Reads/writes the admin-editable AI instructions. Every caller that sends
    /// one of these prompts to a model goes through here instead of reading the
    /// compiled-in constant directly, so an edit made in the UI takes effect on
    /// the next request.
    /// </summary>
    public interface IPromptSettingsService
    {
        /// <summary>
        /// Every known prompt key mapped to its effective text (the stored
        /// value, or the compiled-in default when nothing is stored).
        /// </summary>
        Task<Dictionary<string, string>> GetAllAsync();

        /// <summary>Effective text for one prompt. Never returns empty for a known key.</summary>
        Task<string> GetPromptAsync(string promptKey);

        /// <summary>
        /// Upserts the supplied prompts and returns the full effective map.
        /// Unknown keys are ignored; a blank value resets that prompt to its
        /// compiled-in default by dropping the row.
        /// </summary>
        Task<Dictionary<string, string>> SaveAsync(
            IDictionary<string, string?> values,
            string? updatedBy);

        /// <summary>
        /// When each prompt was last saved and by whom. Keys with no stored row
        /// are absent — those are running on the compiled-in default.
        /// </summary>
        Task<Dictionary<string, (DateTime UpdatedAt, string? UpdatedBy)>> GetMetadataAsync();
    }
}
