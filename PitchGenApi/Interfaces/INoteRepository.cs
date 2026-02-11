using PitchGenApi.Model;
using PitchGenApi.Model.DTOs;
using static NoteRepository;

namespace PitchGenApi.Interfaces
{
    public interface INoteRepository
    {
        // GET
        Task<RepoResponse> GetAllNote(int clientId, int contactId);
        Task<RepoResponse> GetNoteById(int clientId, int contactId, int noteId);

        // ADD
        Task<RepoResponse> AddNote(NotesDto notes);

        // UPDATE
        Task<RepoResponse> UpdateNote(UpdateNotesDto update);

        // DELETE
        Task<RepoResponse> DeleteNote(int clientId, int contactId, int noteId);
    }
}
