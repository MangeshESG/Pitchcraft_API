using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace PitchGenApi.Model
{
    public enum NotificationSource { UnsubscribeByToken, OneClick }

    [System.ComponentModel.DataAnnotations.Schema.Table("Notification")]
    [Index(nameof(UnsubscribedContactId), IsUnique = true)]
    [Index(nameof(ClientId), nameof(Id))]
    public class Notification
    {
        public int Id { get; set; }
        public int ClientId { get; set; }
        public int? ContactId { get; set; }
        [MaxLength(320)] public string Email { get; set; } = "";
        [MaxLength(32)] public string Source { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public DateTime? ReadAt { get; set; }
        public int UnsubscribedContactId { get; set; }
        public UnsubscribedContacts UnsubscribedContact { get; set; } = null!;
    }
}
