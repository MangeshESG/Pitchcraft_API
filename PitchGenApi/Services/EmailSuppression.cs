using Microsoft.EntityFrameworkCore;
using PitchGenApi.Database;

namespace PitchGenApi.Services
{
    /// <summary>
    /// Single source of truth for "may this account still email this person?".
    /// Every outbound path consults it, so an unsubscribe holds no matter which
    /// surface (compose, campaign, sequence, scheduler, reply) triggered the send.
    /// </summary>
    public static class EmailSuppression
    {
        public const string BlockedMessage =
            "This contact has unsubscribed, so no email can be sent to them.";

        /// <summary>
        /// A suppression matches on the contact id OR on the address itself, because
        /// the same person can exist under several contact rows in one account.
        /// </summary>
        public static async Task<bool> IsSuppressedAsync(
            AppDbContext context,
            int clientId,
            int? contactId,
            string? email,
            CancellationToken cancellationToken = default)
        {
            if (context == null || clientId <= 0)
                return false;

            var normalizedEmail = email?.Trim().ToLowerInvariant();
            var hasEmail = !string.IsNullOrWhiteSpace(normalizedEmail);
            var hasContact = contactId.HasValue && contactId.Value > 0;

            if (!hasEmail && !hasContact)
                return false;

            return await context.UnsubscribedContacts
                .AsNoTracking()
                .AnyAsync(u => u.ClientId == clientId &&
                    ((hasContact && u.ContactId == contactId) ||
                     (hasEmail && u.Email != null && u.Email.ToLower() == normalizedEmail)),
                    cancellationToken);
        }
    }
}
