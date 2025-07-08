using PitchGenApi.Model;

namespace PitchGenApi.Interfaces
{
    public interface IPitchService
    {
        Task<PitchResult> GeneratePitchAsync(EnquiryRequest request);
    }
}

