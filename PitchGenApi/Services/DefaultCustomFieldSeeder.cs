using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PitchGenApi.Database;
using PitchGenApi.Model;
using PitchGenApi.Models;

namespace PitchGenApi.Services
{
    public class DefaultCustomFieldSeeder
    {
        private readonly AppDbContext _context;

        public DefaultCustomFieldSeeder(AppDbContext context)
        {
            _context = context;
        }

        public async Task SeedAsync(int clientId)
        {
            var now = DateTime.UtcNow;

            var defaults = new List<CrmCustomField>
            {
                Dropdown(clientId, "Status", "status", new[]
                {
                    "DNC",
                    "Strong follow-up",
                    "Follow-up",
                    "Transferred to another",
                    "Keep for marketing"
                }, now),

                Field(clientId, "When to follow up", "when_to_follow_up", "date", now),

                Dropdown(clientId, "Account", "account", new[]
                {
                    "Company/Division 1",
                    "Company/Division 2",
                    "Company/Division 3"
                }, now),

                Dropdown(clientId, "Contact type", "contact_type", new[]
                {
                    "Client",
                    "Prospect",
                    "Former client",
                    "Former prospect",
                    "Supplier"
                }, now),

                Dropdown(clientId, "Source", "source", new[]
                {
                    "Email campaign",
                    "Google search",
                    "Google AdWords",
                    "LinkedIn",
                    "Referral",
                    "Unknown",
                    "Referral from internal",
                    "Email - single",
                    "Online form"
                }, now),

                Dropdown(clientId, "LinkedIn connection", "linkedin_connection", new[]
                {
                    "Requested",
                    "Accepted",
                    "Requested - checked my profile"
                }, now),

                Field(clientId, "Changed job title", "changed_job_title", "boolean", now),

                Field(clientId, "Changed employer", "changed_employer", "boolean", now),

                Field(clientId, "Next step", "next_step", "longtext", now),

                Dropdown(clientId, "Owner", "owner", new[]
                {
                    "John D",
                    "Jane D"
                }, now),

                Dropdown(clientId, "Invoicing", "invoicing", new[]
                {
                    "To be invoiced",
                    "Invoice(s) sent - outstanding",
                    "Invoice(s) paid - nothing outstanding"
                }, now),

                Dropdown(clientId, "Stage", "stage", new[]
                {
                    "Initial enquiry received",
                    "Initial enquiry responded to",
                    "Ongoing discussions",
                    "Closed - won",
                    "Closed - lost",
                    "To up/cross-sell",
                    "Closing",
                    "Initial email sent",
                    "Initial email opened",
                    "Initial email clicked",
                    "LinkedIn long message sent"
                }, now),

                Field(clientId, "Scope", "scope", "number", now),

                Dropdown(clientId, "DNC reason", "dnc_reason", new[]
                {
                    "Unsubscribed",
                    "Bounceback",
                    "Business reason"
                }, now)
            };

            var existingKeys = await _context.crm_custom_fields
                .Where(x => x.client_id == clientId)
                .Select(x => x.field_key)
                .ToListAsync();

            var fieldsToAdd = defaults
                .Where(x => !existingKeys.Contains(x.field_key))
                .ToList();

            if (!fieldsToAdd.Any())
                return;

            _context.crm_custom_fields.AddRange(fieldsToAdd);
            await _context.SaveChangesAsync();
        }

        private static CrmCustomField Field(
            int clientId,
            string name,
            string key,
            string type,
            DateTime now)
        {
            return new CrmCustomField
            {
                client_id = clientId,
                field_name = name,
                field_key = key,
                field_type = type,
                options_json = "[]",
                created_at = now
            };
        }

        private static CrmCustomField Dropdown(
            int clientId,
            string name,
            string key,
            string[] options,
            DateTime now)
        {
            return new CrmCustomField
            {
                client_id = clientId,
                field_name = name,
                field_key = key,
                field_type = "dropdown",
                options_json = JsonSerializer.Serialize(options),
                created_at = now
            };
        }
    }
}
