using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PitchGenApi.Model
{
    /// <summary>
    /// A saved combination of enhancement columns and an instruction, so a run
    /// a user does every week is one click rather than fifteen ticks and a
    /// retyped paragraph.
    ///
    /// Scoped to the client, which is the account the run panel already works
    /// in: the same person opens the panel from Lists, Views and Segments, and
    /// a preset keyed to anything narrower would be missing from two of the
    /// three. One preset per client may be the default, so the panel can come
    /// up with the usual columns already ticked.
    ///
    /// Created by hand in SQL, like the rest of the Audience Assurance schema:
    ///
    ///   CREATE TABLE dbo.enrichment_presets (
    ///       id            INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    ///       client_id     INT               NOT NULL,
    ///       name          NVARCHAR(200)     NOT NULL,
    ///       fields_json   NVARCHAR(MAX)     NOT NULL CONSTRAINT DF_enrichment_presets_fields DEFAULT N'[]',
    ///       instructions  NVARCHAR(MAX)     NULL,
    ///       is_default    BIT               NOT NULL CONSTRAINT DF_enrichment_presets_default DEFAULT 0,
    ///       created_at    DATETIME2(7)      NOT NULL CONSTRAINT DF_enrichment_presets_created DEFAULT SYSUTCDATETIME(),
    ///       updated_at    DATETIME2(7)      NULL,
    ///       updated_by    NVARCHAR(200)     NULL
    ///   );
    ///
    ///   CREATE UNIQUE INDEX UX_enrichment_presets_client_name
    ///       ON dbo.enrichment_presets (client_id, name);
    ///
    ///   CREATE UNIQUE INDEX UX_enrichment_presets_client_default
    ///       ON dbo.enrichment_presets (client_id) WHERE is_default = 1;
    /// </summary>
    [Table("enrichment_presets")]
    public class EnrichmentPreset
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
        /// The column keys, as a JSON array — <c>["job_title","custom:17"]</c>.
        /// Stored as text rather than a child table because nothing ever
        /// queries one key: the list is read and written whole, by the one
        /// panel that wrote it.
        /// </summary>
        [Column("fields_json")]
        public string FieldsJson { get; set; } = "[]";

        /// <summary>
        /// What the user typed into the run panel's Instructions box. Runs to
        /// a few thousand characters, so nvarchar(max).
        /// </summary>
        [Column("instructions")]
        public string? Instructions { get; set; }

        /// <summary>
        /// Applied when the enhancement check is first chosen. A filtered
        /// unique index keeps at most one default per client.
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
