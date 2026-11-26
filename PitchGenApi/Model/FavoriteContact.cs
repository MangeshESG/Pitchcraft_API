using System.ComponentModel.DataAnnotations.Schema;

namespace PitchGenApi.Models
{
    [Table("crm_favorite_contacts")]
    public class FavoriteContact
    {
        public int client_id { get; set; }
        public int contact_id { get; set; }
    }
}
