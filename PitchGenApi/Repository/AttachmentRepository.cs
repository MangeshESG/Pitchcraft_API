using PitchGenApi.Database;
using PitchGenApi.Interfaces;
using PitchGenApi.Model;

namespace PitchGenApi.Repository
{
    public class AttachmentRepository : IAttachmentRepository
    {
        private readonly AppDbContext _context;

        public AttachmentRepository(AppDbContext context)
        {
            _context = context;
        }

        public async Task<ContactAttachments> AddAttachment(ContactAttachments attachment)
        {
            _context.ContactAttachments.Add(attachment);
            await _context.SaveChangesAsync();
            return attachment;
        }
    }
}
