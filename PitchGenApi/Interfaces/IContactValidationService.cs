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

        /// <summary>
        /// Recovers runs abandoned by a process that died mid-flight: a job left
        /// "running" with a stopped heartbeat goes back to the queue, or is
        /// failed and refunded once it has burned through its attempts.
        ///
        /// Without this a deploy or an app-pool recycle strands a run forever —
        /// it blocks nothing, but it never finishes, never refunds the credits
        /// it reserved, and never tells anyone. Returns how many it touched.
        /// </summary>
        Task<int> ReapStaleJobsAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Atomically claims up to <paramref name="maxJobs"/> queued runs for
        /// this process and flips them to "running" in the same statement, so
        /// a job can never be read as "queued" by one poll and picked up again
        /// by the next before the first has saved — which is what let a single
        /// stuck job be re-claimed every ten seconds forever. Returns the
        /// claimed job ids, oldest first.
        /// </summary>
        Task<List<int>> ClaimQueuedJobsAsync(int maxJobs, string owner, CancellationToken cancellationToken = default);
    }
}
