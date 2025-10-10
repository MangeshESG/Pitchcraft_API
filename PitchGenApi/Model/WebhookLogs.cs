using System.ComponentModel.DataAnnotations;

namespace PitchGenApi.Model
{
    public class WebhookLogs
    {
        [Key]
        public int Id { get; set; }

        [Required]
        [MaxLength(100)]
        public string EventName { get; set; }  // e.g., "Zoho Payment Webhook"

        [Required]
        public string JsonData { get; set; }  // Full webhook payload as string

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
