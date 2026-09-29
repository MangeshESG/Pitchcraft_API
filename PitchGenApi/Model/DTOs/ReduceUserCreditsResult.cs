namespace PitchGenApi.Model.DTOs
{
    /// <summary>
    /// What a "remove credits" attempt did. A failure is an ordinary result
    /// rather than an exception because "they only have 40 left" is something
    /// the admin needs to read, not a fault.
    /// </summary>
    public class ReduceUserCreditsResult
    {
        public bool Success { get; set; }

        public string Message { get; set; } = string.Empty;

        /// <summary>Taken out of the custom-credit bucket.</summary>
        public int RemovedFromCustom { get; set; }

        /// <summary>Taken out of the plan allowance.</summary>
        public int RemovedFromPlan { get; set; }

        /// <summary>Custom credit left afterwards — the balance before it, on a failure.</summary>
        public int RemainingCustom { get; set; }

        /// <summary>Plan credit left afterwards — the balance before it, on a failure.</summary>
        public int RemainingPlan { get; set; }
    }
}
