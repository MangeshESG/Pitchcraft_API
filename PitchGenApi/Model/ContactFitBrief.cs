using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PitchGenApi.Model
{
    /// <summary>
    /// A saved targeting brief the Contact Fit check scores contacts against —
    /// "the company must be an events company that hosts its own events, and
    /// the job title must be one that could buy attendee data", and so on.
    ///
    /// Briefs are standalone and reusable rather than pinned to a list: a
    /// client selling into two audiences needs two briefs, and the same brief
    /// is usually run against several lists, segments and views. One brief per
    /// client may be marked the default so the run panel can preselect it.
    /// </summary>
    [Table("contact_fit_briefs")]
    public class ContactFitBrief
    {
        [Key]
        [Column("id")]
        public int Id { get; set; }

        [Column("client_id")]
        public int ClientId { get; set; }

        [Column("name")]
        [MaxLength(200)]
        public string Name { get; set; } = "";

        /// <summary>
        /// The brief itself, in the client's own words. Runs to thousands of
        /// characters — the example in the spec lists qualifying event types,
        /// disqualifying company types and a long list of job titles — so this
        /// is nvarchar(max).
        /// </summary>
        [Column("brief_text")]
        public string BriefText { get; set; } = "";

        /// <summary>
        /// Preselected in the run panel. A filtered unique index keeps at most
        /// one default per client.
        /// </summary>
        [Column("is_default")]
        public bool IsDefault { get; set; }

        [Column("created_at")]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [Column("updated_at")]
        public DateTime? UpdatedAt { get; set; }

        [Column("updated_by")]
        [MaxLength(200)]
        public string? UpdatedBy { get; set; }
    }
}
