using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PitchGenApi.Model
{
    /// <summary>
    /// One LinkedIn message for a contact, in either direction: krafted and sent
    /// by us, or received from the contact and pasted in by hand (LinkedIn has
    /// no API to sync a reply, so <see cref="Direction"/> is what lets the same
    /// table hold both sides of a conversation in one chronological sequence).
    ///
    /// Append-only history: a re-generate writes a new row, it never
    /// overwrites an old one. The only
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

        /// <summary>
        /// outbound = we sent it. inbound = the contact sent it and someone
        /// pasted it in. Everything written before the conversation feature is
        /// outbound, which the column default takes care of.
        /// </summary>
        [Column("direction")]
        [MaxLength(10)]
        public string Direction { get; set; } = LinkedInMessageDirections.Outbound;

        /// <summary>
        /// SHA-256 of the normalized body, set on pasted inbound rows only.
        /// People re-paste the same chat every time they check it, so without a
        /// content fingerprint one reply ends up stored five times and the model
        /// reads it as five separate messages. A filtered unique index on
        /// (client_id, contact_id, body_hash) makes the second paste a no-op.
        /// Null on outbound rows: sending the same message twice is legitimate.
        /// </summary>
        [Column("body_hash")]
        public byte[]? BodyHash { get; set; }

        /// <summary>CampaignTemplates.Id the message was krafted from; null when typed by hand.</summary>
        [Column("blueprint_id")]
        public int? BlueprintId { get; set; }

        [Column("body")]
        public string Body { get; set; } = "";

        /// <summary>
        /// "This actually happened", rather than "we sent it" — an outbound row
        /// is false until the user ticks Sent, and an inbound row is always true
        /// because a reply someone pasted has by definition already happened.
        /// </summary>
        [Column("is_sent")]
        public bool IsSent { get; set; }

        /// <summary>
        /// UTC, when the message happened on LinkedIn. Outbound: stamped when
        /// the user ticks the Sent checkbox, cleared when they untick it.
        /// Inbound: when the contact sent the reply — defaults to paste time,
        /// but the user can correct it, because a reply pasted a week late still
        /// needs to read as a week old to the model.
        /// </summary>
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

    public static class LinkedInMessageDirections
    {
        public const string Outbound = "outbound";
        public const string Inbound = "inbound";

        public static bool IsKnown(string? direction) =>
            string.Equals(direction, Outbound, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(direction, Inbound, StringComparison.OrdinalIgnoreCase);

        /// <summary>Anything unrecognized is treated as outbound, matching the column default.</summary>
        public static string Normalize(string? direction) =>
            string.Equals(direction, Inbound, StringComparison.OrdinalIgnoreCase)
                ? Inbound
                : Outbound;
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
