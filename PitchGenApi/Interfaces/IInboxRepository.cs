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
        Task<Inboxcredentials?> GetByUserNameAsync(int userId, string username, string protocol);
        Task<List<EmailReplies>> GetRepliesByInboxIdAsync(int inboxId, string Provider);
        Task<List<InboxDropdownDto>> GetInboxPickListByClientIdAsync(int clientId);
        Task<bool> MarkEmailAsReadAsync(string replyId);
        Task<List<EmailThreadDto>> GetInboxThreads(int inboxId, string Provider);


    }
}
