using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PitchGenApi.Model
{
    /// <summary>
    /// What we have already established about a company, reused across every
    /// contact that works there.
    ///
    /// This is the cost lever for Contact Fit. A list with thirty contacts at
    /// one employer does not need that employer researched thirty times — the
    /// expensive question ("does this company own and run its own events?") is
    /// answered once and cached, leaving only the cheap per-person question
    /// ("does this job title fit?") for each contact. Contact Fit therefore
    /// gets steadily cheaper as this table fills.
    ///
    /// Deliberately scoped per client. The classification is written against
    /// that client's brief and their own research, so sharing rows across
    /// tenants would leak one client's audience definition into another's
    /// scores.
    /// </summary>
    [Table("company_intelligence")]
    public class CompanyIntelligence
    {
        [Key]
        [Column("id")]
        public int Id { get; set; }

        [Column("client_id")]
        public int ClientId { get; set; }

        /// <summary>
        /// Lower-cased, punctuation- and suffix-stripped company name. The
        /// fallback key for contacts with no usable website — "Acme Events
        /// Ltd." and "acme events" have to land on the same row.
        /// </summary>
        [Column("company_name_normalised")]
        [MaxLength(300)]
        public string CompanyNameNormalised { get; set; } = "";

        /// <summary>Bare registrable domain, e.g. "acme.com". The preferred key when present.</summary>
        [Column("domain")]
        [MaxLength(255)]
        public string? Domain { get; set; }

        /// <summary>
        /// What the company does, in the model's own words, phrased so it can
        /// be pasted straight back into a later prompt.
        /// </summary>
        [Column("classification")]
        public string? Classification { get; set; }

        /// <summary>Evidence behind the classification, as [{"label","url"}].</summary>
        [Column("sources_json")]
        public string? SourcesJson { get; set; }

        /// <summary>
        /// When this was established. Companies get acquired and rebranded, so
        /// a stale row is re-researched rather than trusted forever.
        /// </summary>
        [Column("researched_at")]
        public DateTime ResearchedAt { get; set; } = DateTime.UtcNow;
    }
}
