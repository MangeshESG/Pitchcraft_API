using System.ComponentModel.DataAnnotations.Schema;

namespace PitchGenApi.Model
{
    [Table("OutgoingMailboxGroups")]
    public class OutgoingMailboxGroup
    {
        public int Id { get; set; }
        public int ClientId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
    }

    [Table("OutgoingMailboxGroupMembers")]
    public class OutgoingMailboxGroupMember
    {
        public int Id { get; set; }
        public int GroupId { get; set; }
        public int OutboxId { get; set; }
        public string Provider { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
    }
}
