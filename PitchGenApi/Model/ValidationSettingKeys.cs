namespace PitchGenApi.Model
{
    /// <summary>
    /// Admin-editable knobs for the Audience Assurance runs, stored as rows in
    /// the app-wide key/value settings table.
    ///
    /// These are tuning dials rather than product settings: they exist so the
    /// batch size can be moved and the effect on accuracy watched, without a
    /// redeploy between each attempt.
    /// </summary>
    public static class ValidationSettingKeys
    {
        /// <summary>
        /// How many contacts go into one model request. The spec's guidance is
        /// 50-100; 50 keeps the returned JSON array comfortably inside
        /// max_output_tokens, since every contact costs output tokens for its
        /// comments.
        ///
        /// Bigger batches mean fewer requests and less prompt repetition, so a
        /// run costs less — but the model has more to hold at once and the
        /// reply has more room to be truncated, which is where accuracy starts
        /// to slip. That trade-off is the whole reason this is adjustable.
        /// </summary>
        public const string BatchSize = "validation_batch_size";

        /// <summary>Used when nothing is stored and nothing is configured.</summary>
        public const int DefaultBatchSize = 50;

        /// <summary>
        /// Bounds on the stored value. One contact per request is legitimate
        /// for a careful comparison; past 200 the reply is long enough that
        /// truncation stops being a risk and becomes the norm.
        /// </summary>
        public const int MinBatchSize = 1;
        public const int MaxBatchSize = 200;

        /// <summary>
        /// A batch size that is safe to run with: out-of-range and unparseable
        /// values fall back to the default rather than failing a run, since
        /// this is read on the path that is already doing paid work.
        /// </summary>
        public static int NormaliseBatchSize(int? value) =>
            value is >= MinBatchSize and <= MaxBatchSize
                ? value.Value
                : DefaultBatchSize;
    }
}
