namespace PitchGenApi.Interfaces
{
    using PitchGenApi.Model.DTOs;
    using PitchGenApi.Services;

    /// <summary>
    /// The four-stage email unlock, shared by the browser extension and the
    /// Audience Assurance email check. See <see cref="EmailUnlockService"/>.
    /// </summary>
    public interface IEmailUnlockService
    {
        /// <summary>
        /// Runs cache, Prospeo, AI search and Hunter in that order, stopping at
        /// the first stage that produces an address.
        /// </summary>
        /// <param name="isAdmin">
        /// Whether the caller may see the trace. It carries the raw prompt and
        /// the raw model reply, so it is attached only when this is true.
        /// </param>
        Task<UnlockEmailResult> UnlockAsync(
            ProspeoUnlockRequestDto request,
            bool isAdmin,
            CancellationToken cancellationToken);

        /// <summary>The AI search stage on its own, for the extension's find-email endpoint.</summary>
        Task<AiEmailSearchOutcome> FindEmailWithAiCoreAsync(
            FindEmailAiRequestDto request,
            int billingClientId);
    }
}
