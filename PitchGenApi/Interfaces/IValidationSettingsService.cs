namespace PitchGenApi.Interfaces
{
    /// <summary>
    /// Reads and writes the admin-editable Audience Assurance tuning values.
    /// Every run reads its batch size through here rather than from a compiled
    /// constant, so a change made in the admin page applies to the next run.
    /// </summary>
    public interface IValidationSettingsService
    {
        /// <summary>
        /// Contacts per model request: the stored value, or the default when
        /// nothing is stored. Never returns an out-of-range number.
        /// </summary>
        Task<int> GetBatchSizeAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Stores the batch size and returns what was actually saved, which is
        /// the requested value clamped into range.
        /// </summary>
        Task<int> SetBatchSizeAsync(int batchSize, string? updatedBy);

        /// <summary>
        /// When the batch size was last saved and by whom. Null while nothing
        /// has ever been saved — that run is on the default.
        /// </summary>
        Task<(DateTime UpdatedAt, string? UpdatedBy)?> GetBatchSizeMetadataAsync();
    }
}
