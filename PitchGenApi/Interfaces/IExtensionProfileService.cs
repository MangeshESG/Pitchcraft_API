namespace PitchGenApi.Interfaces
{
    using PitchGenApi.Model.DTOs;

    /// <summary>
    /// Backs the browser extension's LinkedIn panel: one lookup that answers
    /// "is this profile already in Pitchkraft", a save that creates or patches
    /// the contact, and the professional-summary generation.
    /// </summary>
    public interface IExtensionProfileService
    {
        Task<ExtensionOperationResult> GetProfileContextAsync(
            ExtensionProfileContextRequestDto request);

        Task<ExtensionOperationResult> SaveProfileAsync(
            ExtensionSaveProfileRequestDto request);

        Task<ExtensionOperationResult> GenerateProfileSummaryAsync(
            ExtensionProfileSummaryRequestDto request,
            CancellationToken cancellationToken = default);
    }
}
