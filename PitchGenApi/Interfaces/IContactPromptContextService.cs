using System.Collections.Generic;
using System.Threading.Tasks;
using PitchGenApi.Services;

namespace PitchGenApi.Interfaces
{
    /// <summary>
    /// Resolves the per-contact personalization inputs a generation prompt needs
    /// (notes, past email conversation, professional summary, CRM custom
    /// fields). Channel-agnostic: email and LinkedIn both feed off it.
    /// </summary>
    public interface IContactPromptContextService
    {
        Task<ContactPromptContext> BuildAsync(int clientId, int contactId, string? linkedinInformation);

        Task<Dictionary<string, string>> GetCustomFieldsAsync(int clientId, int contactId);

        /// <summary>
        /// The LinkedIn messages this client has marked as sent to the contact,
        /// formatted as prompt context. Fills {linkedin_messages} in both the
        /// email and the LinkedIn generator.
        ///
        /// Resolved lazily — only called when the blueprint contains the token
        /// AND use_linkedin_message is not "no" — so blueprints that don't ask
        /// for it pay nothing.
        /// </summary>
        Task<LinkedInSentContext> GetSentLinkedInContextAsync(
            int clientId,
            int contactId,
            int maxMessages = 20,
            int maxChars = 6000);
    }
}
