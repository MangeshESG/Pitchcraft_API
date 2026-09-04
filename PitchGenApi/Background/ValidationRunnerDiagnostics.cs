namespace PitchGenApi.Background
{
    /// <summary>
    /// A live record of what the validation runner is actually doing.
    ///
    /// Exists because the runner's own console output is unreachable on the
    /// deployed server: when jobs sat at "queued" for an hour there was no way
    /// to tell, from outside the process, whether the loop had never started,
    /// had crashed, or was running and failing every cycle. Those three have
    /// completely different fixes and look identical from the database.
    ///
    /// Registered as a singleton and written to by the runner, so a controller
    /// can report it over HTTP.
    /// </summary>
    public class ValidationRunnerDiagnostics
    {
        private readonly object _gate = new();

        public DateTime? StartedAt { get; private set; }
        public DateTime? LastPollAt { get; private set; }
        public DateTime? LastClaimAt { get; private set; }
        public DateTime? LastReapAt { get; private set; }
        public int TotalClaimed { get; private set; }
        public int InFlight { get; private set; }
        public int LoopRestarts { get; private set; }
        public string? LastError { get; private set; }
        public DateTime? LastErrorAt { get; private set; }
        public bool Enabled { get; private set; }
        public string? DisabledReason { get; private set; }

        /// <summary>machine:pid, so a scaled-out or recycled host is obvious.</summary>
        public string Owner { get; } = $"{Environment.MachineName}:{Environment.ProcessId}";

        public void MarkStarted()
        {
            lock (_gate)
            {
                StartedAt = DateTime.UtcNow;
                Enabled = true;
                DisabledReason = null;
            }
        }

        public void MarkDisabled(string reason)
        {
            lock (_gate)
            {
                Enabled = false;
                DisabledReason = reason;
            }
        }

        public void MarkPoll(int inFlight)
        {
            lock (_gate)
            {
                LastPollAt = DateTime.UtcNow;
                InFlight = inFlight;
            }
        }

        public void MarkClaimed(int count)
        {
            lock (_gate)
            {
                LastClaimAt = DateTime.UtcNow;
                TotalClaimed += count;
            }
        }

        public void MarkReap()
        {
            lock (_gate) { LastReapAt = DateTime.UtcNow; }
        }

        public void MarkRestart()
        {
            lock (_gate) { LoopRestarts++; }
        }

        public void MarkError(Exception ex)
        {
            lock (_gate)
            {
                LastError = ex.Message;
                LastErrorAt = DateTime.UtcNow;
            }
        }

        public object Snapshot()
        {
            lock (_gate)
            {
                var now = DateTime.UtcNow;

                return new
                {
                    owner = Owner,
                    enabled = Enabled,
                    disabledReason = DisabledReason,
                    startedAt = StartedAt,
                    lastPollAt = LastPollAt,
                    lastClaimAt = LastClaimAt,
                    lastReapAt = LastReapAt,
                    totalClaimed = TotalClaimed,
                    inFlight = InFlight,
                    loopRestarts = LoopRestarts,
                    lastError = LastError,
                    lastErrorAt = LastErrorAt,

                    // The single number worth looking at: the loop polls every
                    // few seconds, so anything above a handful of seconds means
                    // it is not running, whatever the other fields say.
                    secondsSinceLastPoll = LastPollAt is null
                        ? (double?)null
                        : Math.Round((now - LastPollAt.Value).TotalSeconds, 1),

                    isAlive = LastPollAt is not null &&
                              (now - LastPollAt.Value) < TimeSpan.FromSeconds(30)
                };
            }
        }
    }
}
