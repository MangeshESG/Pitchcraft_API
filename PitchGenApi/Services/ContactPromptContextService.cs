using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using PitchGenApi.Database;
using PitchGenApi.Interfaces;
using PitchGenApi.Model;
using Serilog;

namespace PitchGenApi.Services
{
    /// <summary>The three personalization inputs plus how many emails fed them.</summary>
    public sealed class ContactPromptContext
    {
        public string Notes { get; set; } = "";
        public string EmailContext { get; set; } = "";
        public int EmailCount { get; set; }
        public string ProfessionalSummary { get; set; } = "";
    }

    /// <summary>Sent LinkedIn messages rendered as prompt context.</summary>
    public sealed class LinkedInSentContext
    {
        public string Text { get; set; } = "";

        /// <summary>How many messages the text actually contains.</summary>
        public int Count { get; set; }

        /// <summary>Total marked as sent, before the size budget dropped any.</summary>
        public int TotalSent { get; set; }
    }

    /// <summary>A two-sided LinkedIn chat rendered as prompt context.</summary>
    public sealed class LinkedInConversationContext
    {
        public string Text { get; set; } = "";

        /// <summary>How many messages the text actually contains, both directions.</summary>
        public int Count { get; set; }

        /// <summary>Total on record for the contact, before the size budget dropped any.</summary>
        public int TotalMessages { get; set; }

        /// <summary>How many of <see cref="Count"/> came from the contact.</summary>
        public int InboundCount { get; set; }
    }

    /// <inheritdoc cref="IContactPromptContextService"/>
    public class ContactPromptContextService : IContactPromptContextService
    {
        private readonly AppDbContext _dbContext;
        private readonly ContactRepository _contactRepository;
        private readonly INoteRepository _noteRepository;

        public ContactPromptContextService(
            AppDbContext dbContext,
            ContactRepository contactRepository,
            INoteRepository noteRepository)
        {
            _dbContext = dbContext;
            _contactRepository = contactRepository;
            _noteRepository = noteRepository;
        }

        public async Task<ContactPromptContext> BuildAsync(
            int clientId,
            int contactId,
            string? linkedinInformation)
        {
            // Sequential, not Task.WhenAll: both repositories are handed the same
            // scoped AppDbContext, and EF Core allows only one operation on a
            // context at a time. Run in parallel and the loser throws "a second
            // operation was started on this context instance", which the catch
            // blocks below turn into an empty result — the email history then
            // silently vanishes from the prompt.
            var notes = await GetGenerationNotesAsync(clientId, contactId);
            var emailContext = await GetEmailConversationContextAsync(clientId, contactId);

            return new ContactPromptContext
            {
                Notes = notes,
                EmailContext = emailContext.Text,
                EmailCount = emailContext.Count,
                ProfessionalSummary = PromptTextCleaner.StripHtml(linkedinInformation)
            };
        }

        /// <summary>
        /// CRM custom fields as {field_name} → value, scoped to THIS client's
        /// field definitions. A contact that has moved between clients keeps the
        /// old client's values, and two clients' seeded fields share names
        /// ("Status", "Contact type", …) — without the client filter those
        /// collide when keyed by name.
        /// </summary>
        public async Task<Dictionary<string, string>> GetCustomFieldsAsync(int clientId, int contactId)
        {
            var rows = await (
                from value in _dbContext.contact_custom_field_values
                join field in _dbContext.crm_custom_fields
                    on value.field_id equals field.id
                where value.contact_id == contactId
                   && field.client_id == clientId
                select new { field.field_name, value.value }
            ).ToListAsync();

            // Grouped rather than ToDictionary: nothing stops one client holding
            // two fields of the same name, and a duplicate must not fail the
            // whole generation. First non-empty value wins.
            return rows
                .GroupBy(x => x.field_name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(x => x.value).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "",
                    StringComparer.OrdinalIgnoreCase);
        }

        public async Task<LinkedInSentContext> GetSentLinkedInContextAsync(
            int clientId,
            int contactId,
            int maxMessages = 20,
            int maxChars = 6000)
        {
            var empty = new LinkedInSentContext();

            if (clientId <= 0 || contactId <= 0)
                return empty;

            try
            {
                // Drafts are excluded: the point of the placeholder is what the
                // contact has actually seen. Newest-first + Take is a range seek
                // down the clustered key, so a contact with hundreds of messages
                // still costs one cheap read.
                var rows = await _dbContext.LinkedInMessages
                    .AsNoTracking()
                    .Where(m => m.ClientId == clientId && m.ContactId == contactId && m.IsSent)
                    .OrderByDescending(m => m.Id)
                    .Take(Math.Clamp(maxMessages, 1, 100))
                    .Select(m => new { m.Id, m.MessageType, m.SentAt, m.Body })
                    .ToListAsync();

                var totalSent = await _dbContext.LinkedInMessages
                    .AsNoTracking()
                    .CountAsync(m => m.ClientId == clientId && m.ContactId == contactId && m.IsSent);

                if (rows.Count == 0)
                    return empty;

                // Sent order, not generated order — a user can tick an older
                // draft after a newer one.
                var newestFirst = rows
                    .OrderByDescending(r => r.SentAt ?? DateTime.MinValue)
                    .ThenByDescending(r => r.Id)
                    .ToList();

                // Spend the character budget on the most recent messages, then
                // flip to chronological so the model reads them as a sequence.
                var kept = new List<string>();
                var used = 0;
                var dropped = false;

                foreach (var row in newestFirst)
                {
                    var body = (row.Body ?? "").Trim();
                    if (body.Length == 0)
                        continue;

                    var typeLabel = row.MessageType == LinkedInMessageTypes.ConnectionNote
                        ? "Connection request note"
                        : "Direct message";

                    var sentLabel = row.SentAt.HasValue
                        ? row.SentAt.Value.ToString("dd MMM yyyy HH:mm") + " UTC"
                        : "date not recorded";

                    var block = $"Sent: {sentLabel}\nType: {typeLabel}\nMessage:\n{body}";

                    if (used + block.Length > maxChars && kept.Count > 0)
                    {
                        dropped = true;
                        break;
                    }

                    kept.Add(block);
                    used += block.Length;
                }

                if (kept.Count == 0)
                    return empty;

                kept.Reverse();

                var builder = new StringBuilder();

                if (dropped)
                    builder.Append($"(showing the {kept.Count} most recent of {totalSent} LinkedIn messages sent to this contact)\n\n");

                for (var i = 0; i < kept.Count; i++)
                {
                    if (i > 0)
                        builder.Append("\n\n---\n\n");

                    builder.Append($"LinkedIn message {i + 1} of {kept.Count} - SENT BY US\n");
                    builder.Append(kept[i]);
                }

                return new LinkedInSentContext
                {
                    Text = builder.ToString().Trim(),
                    Count = kept.Count,
                    TotalSent = totalSent
                };
            }
            catch (Exception ex)
            {
                // Same reasoning as the email conversation context: a failure
                // here is indistinguishable from "no LinkedIn messages yet", so
                // it gets logged rather than swallowed.
                Log.Error(ex,
                    "Failed to build LinkedIn sent context. ClientId={ClientId}, ContactId={ContactId}",
                    clientId, contactId);
                return empty;
            }
        }

        /// <inheritdoc />
        public async Task<LinkedInConversationContext> GetLinkedInConversationAsync(
            int clientId,
            int contactId,
            int maxMessages = 30,
            int maxChars = 8000)
        {
            var empty = new LinkedInConversationContext();

            if (clientId <= 0 || contactId <= 0)
                return empty;

            try
            {
                // Same shape of read as the sent-only context: a range seek down
                // the clustered key. Both directions live in this table, so the
                // conversation costs exactly one query - no merge of two sources.
                //
                // Drafts are excluded the same way. An outbound row is only part
                // of the conversation once the user says it went out; an inbound
                // row is written with IsSent already true, because a reply
                // someone pasted has by definition happened.
                var rows = await _dbContext.LinkedInMessages
                    .AsNoTracking()
                    .Where(m => m.ClientId == clientId && m.ContactId == contactId && m.IsSent)
                    .OrderByDescending(m => m.Id)
                    .Take(Math.Clamp(maxMessages, 1, 100))
                    .Select(m => new { m.Id, m.Direction, m.SentAt, m.Body })
                    .ToListAsync();

                var totalMessages = await _dbContext.LinkedInMessages
                    .AsNoTracking()
                    .CountAsync(m => m.ClientId == clientId && m.ContactId == contactId && m.IsSent);

                if (rows.Count == 0)
                    return empty;

                // When it happened, not when the row was written: a reply pasted
                // days late must still sort into the position it actually holds
                // in the conversation, or the model reads the exchange backwards.
                var newestFirst = rows
                    .OrderByDescending(r => r.SentAt ?? DateTime.MinValue)
                    .ThenByDescending(r => r.Id)
                    .ToList();

                // Spend the character budget on the most recent exchange, then
                // flip to chronological so the model reads it as a dialogue.
                var kept = new List<(string Block, bool Inbound)>();
                var used = 0;
                var dropped = false;

                foreach (var row in newestFirst)
                {
                    var body = (row.Body ?? "").Trim();
                    if (body.Length == 0)
                        continue;

                    var inbound = string.Equals(
                        row.Direction, LinkedInMessageDirections.Inbound, StringComparison.OrdinalIgnoreCase);

                    // Who said what has to be unmistakable. Without it the model
                    // routinely answers questions the contact never asked, or
                    // writes the next message in the contact's own voice.
                    var speaker = inbound ? "REPLY FROM THEM" : "SENT BY US";
                    var whenLabel = inbound ? "Received" : "Sent";

                    var when = row.SentAt.HasValue
                        ? row.SentAt.Value.ToString("dd MMM yyyy HH:mm") + " UTC"
                        : "date not recorded";

                    var block = $"{speaker}\n{whenLabel}: {when}\nMessage:\n{body}";

                    if (used + block.Length > maxChars && kept.Count > 0)
                    {
                        dropped = true;
                        break;
                    }

                    kept.Add((block, inbound));
                    used += block.Length;
                }

                if (kept.Count == 0)
                    return empty;

                kept.Reverse();

                var builder = new StringBuilder();

                if (dropped)
                    builder.Append($"(showing the {kept.Count} most recent of {totalMessages} LinkedIn messages exchanged with this contact)\n\n");

                for (var i = 0; i < kept.Count; i++)
                {
                    if (i > 0)
                        builder.Append("\n\n---\n\n");

                    builder.Append($"Message {i + 1} of {kept.Count}\n");
                    builder.Append(kept[i].Block);
                }

                return new LinkedInConversationContext
                {
                    Text = builder.ToString().Trim(),
                    Count = kept.Count,
                    TotalMessages = totalMessages,
                    InboundCount = kept.Count(k => k.Inbound)
                };
            }
            catch (Exception ex)
            {
                // Same reasoning as the sent-only context: a failure here is
                // indistinguishable from "no conversation yet", so it gets
                // logged rather than swallowed.
                Log.Error(ex,
                    "Failed to build LinkedIn conversation context. ClientId={ClientId}, ContactId={ContactId}",
                    clientId, contactId);
                return empty;
            }
        }

        // ============================================
        // Internals (mirrors EmailGenerationController)
        // ============================================

        private static readonly JsonSerializerOptions CamelCaseJson = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private sealed class EmailContextResult
        {
            public string Text { get; set; } = "";
            public int Count { get; set; }
        }

        private async Task<EmailContextResult> GetEmailConversationContextAsync(int clientId, int contactId)
        {
            var empty = new EmailContextResult();

            try
            {
                var result = await _contactRepository.GetEmailConversationContextAsync(clientId, contactId);
                if (result == null)
                    return empty;

                var rawJson = JsonSerializer.Serialize(result, CamelCaseJson);
                using var doc = JsonDocument.Parse(rawJson);
                var root = doc.RootElement;

                if (root.ValueKind != JsonValueKind.Object)
                    return empty;

                var emailCount = 0;
                var hasEmailsArray =
                    root.TryGetProperty("emails", out var emailsProp) &&
                    emailsProp.ValueKind == JsonValueKind.Array;

                if (hasEmailsArray)
                    emailCount = emailsProp.GetArrayLength();

                // Preferred: the repository's ready-made prompt context.
                if (root.TryGetProperty("promptContext", out var pc) &&
                    pc.ValueKind == JsonValueKind.String)
                {
                    var promptContext = (pc.GetString() ?? "").Trim();
                    if (!string.IsNullOrWhiteSpace(promptContext))
                        return new EmailContextResult { Text = promptContext, Count = emailCount };
                }

                // Fallback: build a readable thread from the raw emails.
                if (!hasEmailsArray || emailCount == 0)
                    return empty;

                var builder = new StringBuilder();
                var index = 0;

                foreach (var email in emailsProp.EnumerateArray())
                {
                    index++;

                    if (builder.Length > 0)
                        builder.Append("\n\n---\n\n");

                    var direction = ReadStringProperty(email, "direction") == "Sent"
                        ? "SENT BY US"
                        : "RECEIVED FROM CONTACT";

                    builder.Append($"Message {index} - {direction}");

                    AppendEmailLine(builder, email, "sentAt", "Sent");
                    AppendEmailLine(builder, email, "senderEmailId", "From");
                    AppendEmailLine(builder, email, "toEmail", "To");
                    AppendEmailLine(builder, email, "subject", "Subject");

                    var body = PromptTextCleaner.CleanEmailBody(ReadStringProperty(email, "body"));
                    if (!string.IsNullOrWhiteSpace(body))
                        builder.Append($"\nEmail Body:\n{body}");
                }

                return new EmailContextResult
                {
                    Text = builder.ToString().Trim(),
                    Count = emailCount
                };
            }
            catch (Exception ex)
            {
                // Swallowing this quietly makes a failure look identical to
                // "this contact has no past emails", so it gets logged.
                Log.Error(ex,
                    "Failed to build email conversation context. ClientId={ClientId}, ContactId={ContactId}",
                    clientId, contactId);
                return empty;
            }
        }

        private static void AppendEmailLine(StringBuilder builder, JsonElement email, string property, string label)
        {
            var value = ReadStringProperty(email, property);
            if (!string.IsNullOrWhiteSpace(value))
                builder.Append($"\n{label}: {value.Trim()}");
        }

        private static string ReadStringProperty(JsonElement element, string property)
        {
            if (element.ValueKind != JsonValueKind.Object)
                return "";

            if (!element.TryGetProperty(property, out var prop))
                return "";

            return prop.ValueKind switch
            {
                JsonValueKind.String => prop.GetString() ?? "",
                JsonValueKind.Number => prop.ToString(),
                _ => ""
            };
        }

        private async Task<string> GetGenerationNotesAsync(int clientId, int contactId)
        {
            try
            {
                var result = await _noteRepository.GetAllNote(clientId, contactId);
                if (result == null)
                    return "";

                var rawJson = JsonSerializer.Serialize(result, CamelCaseJson);
                using var doc = JsonDocument.Parse(rawJson);
                var root = doc.RootElement;

                if (!root.TryGetProperty("success", out var successProp) || !successProp.GetBoolean())
                    return "";

                if (!root.TryGetProperty("data", out var dataProp) || dataProp.ValueKind != JsonValueKind.Array)
                    return "";

                var usableNotes = new List<string>();

                foreach (var item in dataProp.EnumerateArray())
                {
                    var useInGeneration = item.TryGetProperty("isUseInGenration", out var useProp)
                        && useProp.ValueKind == JsonValueKind.True;

                    if (!useInGeneration)
                        continue;

                    var note = item.TryGetProperty("note", out var noteProp)
                        ? noteProp.GetString() ?? ""
                        : "";

                    if (string.IsNullOrWhiteSpace(note))
                        continue;

                    var cleaned = PromptTextCleaner.StripHtml(note);

                    if (!string.IsNullOrWhiteSpace(cleaned))
                        usableNotes.Add(cleaned);
                }

                return string.Join("\n", usableNotes);
            }
            catch (Exception ex)
            {
                Log.Error(ex,
                    "Failed to build generation notes. ClientId={ClientId}, ContactId={ContactId}",
                    clientId, contactId);
                return "";
            }
        }
    }
}
