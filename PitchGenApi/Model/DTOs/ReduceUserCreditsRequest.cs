namespace PitchGenApi.Model.DTOs
{
    /// <summary>
    /// "Remove credits" from the admin page. The admin doing the removing is
    /// identified from their token, so nothing here says who the caller is.
    /// </summary>
    public class ReduceUserCreditsRequest
    {
        /// <summary>Client whose balance is being reduced.</summary>
        public int UserId { get; set; }

        /// <summary>How many credits to take off. Must be positive.</summary>
        public int CreditsCount { get; set; }

        /// <summary>Optional note for the log, e.g. "refunded in Stripe".</summary>
        public string? Reason { get; set; }
    }
}
