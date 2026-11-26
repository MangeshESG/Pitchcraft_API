using System.ComponentModel.DataAnnotations.Schema;
using System.ComponentModel.DataAnnotations;

namespace PitchGenApi.Models
{
    [Table("crm_favorite_collections")]
    public class FavoriteCollection
    {
        public int client_id { get; set; }
        [MaxLength(4)] public string item_type { get; set; } = "";
        public int item_id { get; set; }
    }
}
