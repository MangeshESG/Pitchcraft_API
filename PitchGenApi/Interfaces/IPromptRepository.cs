using Microsoft.Identity.Client;
using PitchGenApi.Model;
using Prompt = PitchGenApi.Model.Prompt;

namespace PitchGenApi.Interfaces
{
    public interface IPromptRepository
    {
        Task<IEnumerable<Prompt>> GetAllPromptsByUserIdAsync(int userId);
        Task<Prompt> GetPromptByIdAsync(int id);
        Task<Prompt> AddPromptAsync(Prompt prompt);
        Task<Prompt> UpdatePromptAsync(Prompt prompt);
        Task<bool> DeletePromptAsync(int id);
    }
}
