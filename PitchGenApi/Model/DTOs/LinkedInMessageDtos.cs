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

        /// <summary>message | connection_note. Defaults to message.</summary>
        public string? MessageType { get; set; }

        /// <summary>
        /// Character cap for the generated text. Omit to use the default for the
        /// message type (300 for a connection note, 8000 for a message).
        /// </summary>
        public int? MaxLength { get; set; }

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
        public string? MessageType { get; set; }
        public int? BlueprintId { get; set; }
    }

    /// <summary>POST api/linkedin-messages/mark-sent — the Sent checkbox.</summary>
    public class MarkLinkedInMessageSentRequest
    {
        public int ClientId { get; set; }

        /// <summary>Either identifies the row. Uid is preferred — it makes retries idempotent.</summary>
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
