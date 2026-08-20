using System;
using System.Collections.Generic;

namespace PitchGenApi.Model.DTOs
{
    /// <summary>POST api/linkedin-messages/generate</summary>
    public class GenerateLinkedInMessageRequest
    {
        public int ClientId { get; set; }
        public int ContactId { get; set; }

        /// <summary>CampaignTemplates.Id — the blueprint to kraft from.</summary>
        public int BlueprintId { get; set; }

        /// <summary>true = no DB row, no credit deducted. For "try it" previews.</summary>
        public bool Preview { get; set; }

        /// <summary>extension | web — recorded on the row when it is marked sent.</summary>
        public string? Source { get; set; }
    }

    /// <summary>POST api/linkedin-messages/save — store a hand-written message, or edit a draft.</summary>
    public class SaveLinkedInMessageRequest
    {
        public int ClientId { get; set; }
        public int ContactId { get; set; }

        /// <summary>Omit to create a new row; supply either id or uid to edit an existing one.</summary>
        public long? MessageId { get; set; }
        public Guid? MsgUid { get; set; }

        public string Body { get; set; } = "";
        public int? BlueprintId { get; set; }
    }

    /// <summary>POST api/linkedin-messages/mark-sent — the Sent checkbox.</summary>
    public class MarkLinkedInMessageSentRequest
    {
        public int ClientId { get; set; }

        /// <summary>
        /// The message itself, for the first tick. Generation stores nothing, so
        /// a message that has never been ticked has no row yet and these fields
        /// are what create it. Ignored once the row exists and is already sent.
        /// </summary>
        public int ContactId { get; set; }
        public string? Body { get; set; }
        public int? BlueprintId { get; set; }

        /// <summary>Either identifies an existing row. The uid handed out by generate is preferred: it makes retries idempotent and survives the first tick.</summary>
        public long? MessageId { get; set; }
        public Guid? MsgUid { get; set; }

        /// <summary>true = ticked, false = unticked (clears the sent timestamp).</summary>
        public bool IsSent { get; set; } = true;

        /// <summary>extension | web</summary>
        public string? MarkedFrom { get; set; }

        /// <summary>
        /// Optional: when the user actually ticked it, for a checkbox queued
        /// while offline. Ignored unless it is in the past and within the last
        /// 7 days — otherwise the server clock wins.
        /// </summary>
        public DateTime? OccurredAtUtc { get; set; }
    }

    /// <summary>
    /// POST api/linkedin-messages/import — a message that happened on LinkedIn
    /// outside Pitchkraft, pasted in by hand.
    ///
    /// Usually the contact's reply, which is the whole point: there is no
    /// LinkedIn API to sync one, and without it the AI writes every follow-up as
    /// though nobody ever answered. Also takes an outbound message sent straight
    /// from LinkedIn rather than through Kraft.
    /// </summary>
    public class ImportLinkedInMessageRequest
    {
        public int ClientId { get; set; }
        public int ContactId { get; set; }

        /// <summary>inbound = they sent it, outbound = we did. Defaults to inbound.</summary>
        public string? Direction { get; set; }

        /// <summary>The pasted text. One message per call — see the endpoint remarks.</summary>
        public string Body { get; set; } = "";

        /// <summary>
        /// When it happened on LinkedIn. Omit for "now". Worth sending: a reply
        /// pasted a week late still needs to read as a week old to the model.
        /// </summary>
        public DateTime? OccurredAtUtc { get; set; }

        /// <summary>extension | web</summary>
        public string? Source { get; set; }
    }

    /// <summary>POST api/linkedin-messages/summary — one call for a whole grid page.</summary>
    public class LinkedInMessageSummaryRequest
    {
        public int ClientId { get; set; }
        public List<int> ContactIds { get; set; } = new();
    }

    /// <summary>POST api/linkedin-messages/delete</summary>
    public class DeleteLinkedInMessageRequest
    {
        public int ClientId { get; set; }
        public long? MessageId { get; set; }
        public Guid? MsgUid { get; set; }
    }
}
