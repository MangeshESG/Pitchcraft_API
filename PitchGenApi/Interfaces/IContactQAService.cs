using PitchGenApi.Model.DTOs;

namespace PitchGenApi.Interfaces
{
    public interface IContactQAService
    {
        Task<ContactQAResponse> AskAsync(ContactQARequest request);
    }
}
