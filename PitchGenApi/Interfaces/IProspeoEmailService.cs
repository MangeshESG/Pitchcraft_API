namespace PitchGenApi.Interfaces
{
    using PitchGenApi.Model.DTOs;

    /// <summary>
    /// Prospeo person enrichment (https://prospeo.io), asked for a verified
    /// address against a LinkedIn profile URL.
    ///
    /// Two callers share it: the extension's unlock button, which tries
    /// Prospeo before falling back to an AI search, and the Audience Assurance
    /// email verification check, which tries Prospeo before falling back to
    /// Hunter. The call and the verdict logic live here so the two cannot
    /// drift apart; the credit handling, caching and diagnostics around it
    /// stay with each caller, because they differ.
    /// </summary>
    public interface IProspeoEmailService
    {
        /// <summary>False when no Prospeo:ApiKey is configured; the stage is then skipped.</summary>
        bool IsConfigured { get; }

        /// <summary>
        /// Asks Prospeo about one LinkedIn profile. Never throws: transport
        /// failures, error payloads and "no verified address" all come back as
        /// a <see cref="ProspeoLookupResult"/> with RejectedBecause set,
        /// because every caller treats this as one stage of a cascade and must
        /// be able to move on to the next.
        /// </summary>
        Task<ProspeoLookupResult> FindEmailAsync(
            string linkedInUrl,
            CancellationToken cancellationToken = default);
    }
}
