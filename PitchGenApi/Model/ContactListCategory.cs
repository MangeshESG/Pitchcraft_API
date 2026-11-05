using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace PitchGenApi.Models
{
    [Table("crm_contact_list_categories")]
    [Index(nameof(client_id), nameof(name), IsUnique = true)]
    public class ContactListCategory
    {
        public int id { get; set; }
        public int client_id { get; set; }
        [MaxLength(120)] public string name { get; set; } = "";
        public DateTime created_at { get; set; }
    }
}
