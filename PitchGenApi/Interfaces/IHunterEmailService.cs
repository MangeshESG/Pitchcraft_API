namespace PitchGenApi.Interfaces
{
    using PitchGenApi.Model.DTOs;

    /// <summary>
    /// Hunter.io email lookup (https://hunter.io/api-documentation/v2).
    ///
    /// Sits behind the AI search as the last stage of the unlock flow: it is
    /// asked only when the model's own confidence in its best candidate falls
    /// below <see cref="ConfidenceThreshold"/>, so a confident AI answer never
    /// spends a Hunter request.
    /// </summary>
    public interface IHunterEmailService
    {
        /// <summary>False when no Hunter:ApiKey is configured; the stage is then skipped.</summary>
        bool IsConfigured { get; }

        /// <summary>
        /// The AI confidence at or above which Hunter is not consulted.
        /// Configurable as Hunter:ConfidenceThreshold, 80 by default.
        /// </summary>
        int ConfidenceThreshold { get; }

        /// <summary>
        /// Runs Hunter's Email Finder. Never throws: transport failures, error
        /// payloads and "no result" all come back as a
        /// <see cref="HunterLookupResult"/> with RejectedBecause set, because
        /// this is a fallback and must not fail the unlock it is helping.
        /// </summary>
        Task<HunterLookupResult> FindEmailAsync(
            HunterLookupRequest request,
            CancellationToken cancellationToken = default);
    }
}
