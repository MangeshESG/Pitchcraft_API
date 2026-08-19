using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PitchGenApi.Model
{
    /// <summary>
    /// One generated LinkedIn message for a contact. Append-only history: a
    /// re-generate writes a new row, it never overwrites an old one. The only
    /// columns that change after insert are the ones the "Sent" checkbox owns
    /// (<see cref="IsSent"/>, <see cref="SentAt"/>, <see cref="MarkedFrom"/>),
    /// and none of them are part of the clustered key, so the update happens in
    /// place.
    ///
    /// Emails are NOT stored here — they stay on contacts.email_body /
    /// contacts.email_subject exactly as before.
    /// </summary>
    [Table("linkedin_messages")]
    public class LinkedInMessage
    {
        [Key]
        [Column("id")]
        public long Id { get; set; }

        [Column("client_id")]
        public int ClientId { get; set; }

        [Column("contact_id")]
        public int ContactId { get; set; }

        /// <summary>
        /// Always "message" on anything written now - the type picker is gone and
        /// the blueprint decides how long a message runs. Rows written before that
        /// can still say "connection_note", and the history views read it.
        /// </summary>
        [Column("message_type")]
        [MaxLength(20)]
        public string MessageType { get; set; } = LinkedInMessageTypes.Message;

        /// <summary>CampaignTemplates.Id the message was krafted from; null when typed by hand.</summary>
        [Column("blueprint_id")]
        public int? BlueprintId { get; set; }

        [Column("body")]
        public string Body { get; set; } = "";

        [Column("is_sent")]
        public bool IsSent { get; set; }

        /// <summary>UTC. Stamped when the user ticks the Sent checkbox, cleared when they untick it.</summary>
        [Column("sent_at")]
        public DateTime? SentAt { get; set; }

        /// <summary>extension | web — which surface ticked the checkbox.</summary>
        [Column("marked_from")]
        [MaxLength(20)]
        public string? MarkedFrom { get; set; }

        [Column("generated_at")]
        public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Generated client-side and unique per client, so a retried or
        /// double-tapped mark-sent call lands on the same row instead of
        /// creating a second "sent" record.
        /// </summary>
        [Column("msg_uid")]
        public Guid MsgUid { get; set; } = Guid.NewGuid();
    }

    public static class LinkedInMessageTypes
    {
        public const string Message = "message";
        public const string ConnectionNote = "connection_note";

        /// <summary>
        /// Only the read paths still need this: the by-contact filter and the
        /// history labels, both of which look at rows written before the type
        /// picker was removed. Nothing written now is anything but Message.
        /// </summary>
        public static string Normalize(string? messageType) =>
            string.Equals(messageType, ConnectionNote, StringComparison.OrdinalIgnoreCase)
                ? ConnectionNote
                : Message;
    }
}
