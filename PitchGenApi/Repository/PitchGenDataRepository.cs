using Microsoft.EntityFrameworkCore;
using PitchGenApi.Database;
using PitchGenApi.Model;
using PitchGenApi.Interfaces;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;



namespace PitchGenApi.Repository
{
   
    public class PitchGenDataRepository : IPitchGenDataRepository
    {
        private readonly AppDbContext _context;

        public PitchGenDataRepository(AppDbContext context) // Constructor injection
        {
            _context = context;
        }
        public async Task<List<PitchGendata>> GetAllPitchGenDataAsync() // Corrected method signature
        {
            return await _context.PitchGendata.ToListAsync(); // Use ToListAsync for all rows
        }

    }
}
