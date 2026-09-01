namespace PitchGenApi.Interfaces
{
    using PitchGenApi.Model.DTOs;

    /// <summary>
    /// Runs the Audience Assurance checks over selected contacts.
    ///
    /// Queueing and running are separate on purpose. A hundred contacts with
    /// web search enabled takes minutes, which is far longer than a request
    /// should be held open, so the API queues a job and returns immediately
    /// and the background worker does the work.
    /// </summary>
    public interface IContactValidationService
    {
        /// <summary>
        /// Validates the request, reserves the credits and writes the job.
        /// Returns the queued job, or throws <see cref="InvalidOperationException"/>
        /// with a message meant for the user when the run cannot start —
        /// no brief chosen, no prompt configured, not enough credit.
        /// </summary>
        Task<ValidationJobDto> QueueAsync(RunValidationRequestDto request);

        /// <summary>
        /// Executes one queued job to completion, writing results as each batch
        /// lands so a long run shows progress and a crash loses only the batch
        /// in flight. Never throws: a failure is recorded on the job.
        /// </summary>
        Task ProcessJobAsync(int jobId, CancellationToken cancellationToken = default);
    }
}
