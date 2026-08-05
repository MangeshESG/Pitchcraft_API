using System.ComponentModel.DataAnnotations.Schema;
using System.ComponentModel.DataAnnotations;

namespace PitchGenApi.Model
{
    public class EmailPattern
    {
        [Key]
        [Column("id")]
        public int Id { get; set; }

        [Column("domain_id")]
        public int DomainId { get; set; }

        [Column("email_pattern")]
        public string EmailPatternName { get; set; }

        [ForeignKey(nameof(DomainId))]
        public virtual Domain Domain { get; set; }
    }
}
