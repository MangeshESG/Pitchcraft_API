using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PitchGenApi.Model
{
    /// <summary>
    /// One validation run, and what it actually cost.
    ///
    /// The cost columns are the point of this table. Web search dominates the
    /// price of a run — roughly a cent per search against a fifth of a cent of
    /// tokens for a whole 100-contact batch — and how many searches a batch
    /// needs cannot be predicted, only measured. Every run therefore records
    /// its real token usage and its real search count so cost per 100 contacts
    /// can be derived from production data before credit pricing is set.
    /// </summary>
    [Table("contact_validation_jobs")]
    public class ContactValidationJob
    {
        [Key]
        [Column("id")]
        public int Id { get; set; }

        [Column("client_id")]
        public int ClientId { get; set; }

        /// <summary>One of <see cref="ValidationCheckTypes"/>.</summary>
        [Column("check_type")]
        [MaxLength(50)]
        public string CheckType { get; set; } = "";

        /// <summary>The brief scored against; only ever set for contact fit.</summary>
        [Column("brief_id")]
        public int? BriefId { get; set; }

        [Column("model_name")]
        [MaxLength(200)]
        public string? ModelName { get; set; }

        /// <summary>openai | deepseek | prospeo — which backend actually ran it.</summary>
        [Column("provider")]
        [MaxLength(50)]
        public string? Provider { get; set; }

        /// <summary>One of <see cref="ValidationJobStatuses"/>.</summary>
        [Column("status")]
        [MaxLength(20)]
        public string Status { get; set; } = ValidationJobStatuses.Queued;

        [Column("contact_count")]
        public int ContactCount { get; set; }

        [Column("processed_count")]
        public int ProcessedCount { get; set; }

        [Column("failed_count")]
        public int FailedCount { get; set; }

        [Column("input_tokens")]
        public int InputTokens { get; set; }

        /// <summary>
        /// Cached prompt tokens, billed at a small fraction of the miss rate.
        /// Reported separately because a batch that reuses one brief across
        /// many chunks should show most of its input as cache hits — if it
        /// doesn't, the prompt assembly is wrong.
        /// </summary>
        [Column("cached_tokens")]
        public int CachedTokens { get; set; }

        [Column("output_tokens")]
        public int OutputTokens { get; set; }

        [Column("total_tokens")]
        public int TotalTokens { get; set; }

        /// <summary>
        /// Actual server-side web searches performed, counted from the
        /// provider's tool trace. Cost scales with this, not with contacts.
        /// </summary>
        [Column("web_search_calls")]
        public int WebSearchCalls { get; set; }

        /// <summary>Tokens priced from ModelRates, plus searches at the configured per-call rate.</summary>
        [Column("calculated_cost")]
        public decimal CalculatedCost { get; set; }

        [Column("credits_charged")]
        public int CreditsCharged { get; set; }

        [Column("elapsed_ms")]
        public int ElapsedMs { get; set; }

        [Column("error_message")]
        public string? ErrorMessage { get; set; }

        [Column("created_at")]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [Column("started_at")]
        public DateTime? StartedAt { get; set; }

        [Column("completed_at")]
        public DateTime? CompletedAt { get; set; }

        [Column("created_by")]
        [MaxLength(200)]
        public string? CreatedBy { get; set; }
    }

    /// <summary>
    /// One contact inside a run. Exists so a partly-failed job can be reported
    /// honestly and refunded in proportion: a model that skips 12 of 50
    /// contacts should not be charged for them.
    /// </summary>
    [Table("contact_validation_job_items")]
    public class ContactValidationJobItem
    {
        [Key]
        [Column("id")]
        public int Id { get; set; }

        [Column("job_id")]
        public int JobId { get; set; }

        [Column("contact_id")]
        public int ContactId { get; set; }

        /// <summary>One of <see cref="ValidationItemStatuses"/>.</summary>
        [Column("status")]
        [MaxLength(20)]
        public string Status { get; set; } = ValidationItemStatuses.Pending;

        [Column("error")]
        public string? Error { get; set; }
    }
}
