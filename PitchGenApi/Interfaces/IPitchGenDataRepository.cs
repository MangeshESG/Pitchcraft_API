using PitchGenApi.Model;

namespace PitchGenApi.Interfaces
{
    public interface IPitchGenDataRepository
    {
        Task<List<PitchGendata>> GetAllPitchGenDataAsync();
    }
}
