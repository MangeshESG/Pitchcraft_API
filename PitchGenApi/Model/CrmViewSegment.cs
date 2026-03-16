using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PitchGenApi.Model
{
    [Table("crm_view_segments")]
    public class CrmViewSegment
    {
        [Key]
        public int id { get; set; }

        public int view_id { get; set; }

        public int segment_id { get; set; }
    }
}