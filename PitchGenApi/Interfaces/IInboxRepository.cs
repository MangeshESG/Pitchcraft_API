using MailKit.Security;
using PitchGenApi.Model;
using PitchGenApi.Model.DTOs;

namespace PitchGenApi.Interfaces
{
    public interface IInboxRepository
    {
        Task<Inboxcredentials?> GetByIdAsync(int id);
        Task<List<Inboxcredentials>> GetByUserIdAsync(int clientId);
        Task<IEnumerable<Inboxcredentials>> GetAllAsync();
        Task AddAsync(Inboxcredentials setting);
        Task UpdateAsync(Inboxcredentials setting);
        Task DeleteAsync(int id);
        Task<bool> ValidateAsync(InboxcredentialsDTO dto);
        Task<Inboxcredentials?> GetByUserNameAsync(int userId, string username);
        Task<List<EmailReplies>> GetRepliesByInboxIdAsync(int inboxId, string Provider);
        Task<List<InboxDropdownDto>> GetInboxPickListByClientIdAsync(int clientId);
        Task<bool> MarkEmailAsReadAsync(string replyId);
        Task<bool> MarkEmailAsUnassignedReadAsync(string messageId);
        Task<PagedInboxEmailDto> GetInboxThreads(int inboxId, int clientId, string Provider, int pageNumber = 1, int pageSize = 10);
        SecureSocketOptions GetSecureOption(string encryption);
        Task<string> DeleteConversationAsync(DeleteConversationDto dto);
        Task<PagedInboxEmailDto> GetInboxEmails(int clientId, int inboxId, string Provider, int pageNumber = 1, int pageSize = 10);
        Task<PagedInboxEmailDto> GetCombinedInboxThreadsAsync(int clientId, int inboxId, string provider, int pageNumber = 1, int pageSize = 10);
        Task<PagedInboxEmailDto> GetSentOnlyThreads(int inboxId, string Provider, int pageNumber = 1, int pageSize = 10);
        // Interface
        Task<TotalUnreadCountDto> GetTotalUnreadCountAsync(int clientId);
        Task<bool> CreateInboxCredentialsAsync(InboxcredentialsDTO dto);
        Task<string> TogglePinAsync(int clientId, Guid trackingId);
        Task<List<EmailThreadDto>> GetPinnedEmails(int clientId, int contactId);
        Task<string?> GetLatestEmailTrailAsync(Guid trackingId);
    }
}
