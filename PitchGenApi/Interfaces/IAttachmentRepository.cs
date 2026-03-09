using PitchGenApi.Model;

namespace PitchGenApi.Interfaces
{
    public interface IAttachmentRepository
    {
        Task<ContactAttachments> AddAttachment(ContactAttachments attachment);
        Task<ContactAttachments> GetById(int id);

    }
}
