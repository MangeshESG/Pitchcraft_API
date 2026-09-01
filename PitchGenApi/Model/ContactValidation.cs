using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PitchGenApi.Model
{
    /// <summary>
    /// The current validation state of one contact: the latest score, comments
    /// and check date for each of the four Audience Assurance checks.
    ///
    /// One wide row per contact rather than one row per check, because the
    /// contact list grid needs all four scores in a single left join — the
    /// hottest query in the product. A row-per-check table would force a pivot
    /// there for no gain, since only the latest result of each check is ever
    /// shown. The per-run history lives in contact_validation_jobs instead.
    ///
    /// A check that has never run leaves its own columns null and does not
    /// touch the others: the checks are independent and one score must never
    /// be derived from another.
    /// </summary>
    [Table("contact_validations")]
    public class ContactValidation
    {
        [Key]
        [Column("id")]
        public int Id { get; set; }

        [Column("client_id")]
        public int ClientId { get; set; }

        [Column("contact_id")]
        public int ContactId { get; set; }

        // ---------------- Contact fit ----------------

        [Column("contact_fit_confidence")]
        public int? ContactFitConfidence { get; set; }

        [Column("contact_fit_comments")]
        public string? ContactFitComments { get; set; }

        /// <summary>Which brief produced the score — a score means nothing without it.</summary>
        [Column("contact_fit_brief_id")]
        public int? ContactFitBriefId { get; set; }

        [Column("contact_fit_checked_at")]
        public DateTime? ContactFitCheckedAt { get; set; }

        // ---------------- Data integrity ----------------

        [Column("data_integrity_confidence")]
        public int? DataIntegrityConfidence { get; set; }

        /// <summary>
        /// Problems only. The spec is explicit that this must never say "all
        /// fields are complete" or "no duplicates found" — a clean record
        /// returns an empty string and lets the score carry the meaning.
        /// </summary>
        [Column("data_integrity_comments")]
        public string? DataIntegrityComments { get; set; }

        [Column("data_integrity_checked_at")]
        public DateTime? DataIntegrityCheckedAt { get; set; }

        // ---------------- Live contact ----------------

        [Column("live_contact_confidence")]
        public int? LiveContactConfidence { get; set; }

        [Column("live_contact_comments")]
        public string? LiveContactComments { get; set; }

        [Column("live_contact_checked_at")]
        public DateTime? LiveContactCheckedAt { get; set; }

        // ---------------- Email discovery and verification ----------------

        [Column("email_validity_confidence")]
        public int? EmailValidityConfidence { get; set; }

        /// <summary>Provider status verbatim, e.g. Prospeo's "VERIFIED" or Hunter's "risky".</summary>
        [Column("email_validity_status")]
        [MaxLength(50)]
        public string? EmailValidityStatus { get; set; }

        /// <summary>prospeo | hunter | none — which stage of the cascade answered.</summary>
        [Column("email_validity_source")]
        [MaxLength(30)]
        public string? EmailValiditySource { get; set; }

        [Column("email_validity_comments")]
        public string? EmailValidityComments { get; set; }

        [Column("email_checked_at")]
        public DateTime? EmailCheckedAt { get; set; }

        // ---------------- Shared ----------------

        /// <summary>
        /// Merged evidence across checks, as [{"label","url"}]. Kept as one list
        /// rather than per check because the same team page routinely supports
        /// both the fit and the live-contact verdict, and the UI shows one
        /// source list per contact.
        /// </summary>
        [Column("sources_json")]
        public string? SourcesJson { get; set; }

        /// <summary>
        /// Set by hand from "Mark as verified" — the user has checked this
        /// contact themselves, or corrected it after the AI got it wrong.
        ///
        /// A later run never clears this. Overriding a mistaken AI correction
        /// is the entire point of the flag, so the UI shows the new score
        /// beside the manual mark and its date rather than silently discarding
        /// the human judgement.
        /// </summary>
        [Column("is_verified")]
        public bool IsVerified { get; set; }

        [Column("verified_at")]
        public DateTime? VerifiedAt { get; set; }

        [Column("verified_by")]
        [MaxLength(200)]
        public string? VerifiedBy { get; set; }

        [Column("created_at")]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [Column("updated_at")]
        public DateTime? UpdatedAt { get; set; }
    }
}
