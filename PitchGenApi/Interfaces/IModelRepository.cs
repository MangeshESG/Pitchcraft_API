using Microsoft.EntityFrameworkCore;
using PitchGenApi.Database;
using PitchGenApi.Model;

namespace PitchGenApi.Interfaces
{
    public interface IModelRepository
    {
        Task<List<ModelInfoDto>> GetAllModelInfoAsync();
    }

    public class ModelRepository : IModelRepository
    {
        private readonly AppDbContext _context;

        public ModelRepository(AppDbContext context)
        {
            _context = context;
        }

        public async Task<List<ModelInfoDto>> GetAllModelInfoAsync()
        {
            return await _context.ModelRates
                .Select(m => new ModelInfoDto
                {
                    ModelName = m.ModelName,
                    InputPrice = m.InputPrice,
                    OutputPrice = m.OutputPrice
                })
                .ToListAsync();
        }
    }
}
