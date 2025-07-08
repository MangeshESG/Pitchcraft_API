using Microsoft.EntityFrameworkCore;
using PitchGenApi.Database;
using PitchGenApi.Model;
using PitchGenApi.Interfaces;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace PitchGenApi.Repository
{
    public class PromptRepository : IPromptRepository
    {
        private readonly AppDbContext _context;

        public PromptRepository(AppDbContext context)
        {
            _context = context;
        }

        // ✅ Matches `Task<IEnumerable<Prompt>>`
        public async Task<IEnumerable<Prompt>> GetAllPromptsAsync()
        {
            return await _context.Prompts.ToListAsync();
        }

        // ✅ Matches `Task<IEnumerable<Prompt>>`
        public async Task<IEnumerable<Prompt>> GetAllPromptsByUserIdAsync(int userId)
        {
            return await _context.Prompts
                .Where(p => p.UserId == userId)
                .ToListAsync();
        }

        public async Task<Prompt> GetPromptByIdAsync(int id)
        {
            return await _context.Prompts.FindAsync(id);
        }

        // ✅ Changed `CreatePromptAsync` to `AddPromptAsync` to match the interface
        public async Task<Prompt> AddPromptAsync(Prompt prompt)
        {
           
            if (prompt == null || prompt.UserId <= 0)
                throw new ArgumentException("Invalid prompt data");

            // Ensure CreatedAt is set if not already provided
            prompt.CreatedAt ??= DateTime.UtcNow;

            _context.Prompts.Add(prompt);
            await _context.SaveChangesAsync();

            return prompt;
        }


        // ✅ Matches `Task<Prompt>` as expected
        public async Task<Prompt> UpdatePromptAsync(Prompt prompt)
        {
            var existingPrompt = await _context.Prompts.FindAsync(prompt.Id);
            if (existingPrompt == null) return null; // Ensure prompt exists

            _context.Entry(existingPrompt).CurrentValues.SetValues(prompt);
            await _context.SaveChangesAsync();
            return existingPrompt;
        }

        // ✅ Matches `Task<bool>`
        public async Task<bool> DeletePromptAsync(int id)
        {
            var prompt = await _context.Prompts.FindAsync(id);
            if (prompt == null) return false;

            _context.Prompts.Remove(prompt);
            await _context.SaveChangesAsync();
            return true;
        }
    }
}
