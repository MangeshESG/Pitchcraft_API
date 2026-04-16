using PitchGenApi.Model;
using PitchGenApi.Model.DTOs;

namespace PitchGenApi.Interfaces
{
    public interface IInboxEmailService
    {
        Task<bool> TestConnectionAsync(Inboxcredentials setting);
        Task<List<EmailMessageDto>> GetEmailsAsync(Inboxcredentials setting);
    }
}
