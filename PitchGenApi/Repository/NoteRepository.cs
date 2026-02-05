using Microsoft.EntityFrameworkCore;
using PitchGenApi.Database;
using PitchGenApi.Interfaces;
using PitchGenApi.Model;
using PitchGenApi.Model.DTOs;

public class NoteRepository : INoteRepository
{
    private readonly AppDbContext _context;
    private readonly ILogger<NoteRepository> _logger;

    public NoteRepository(AppDbContext context, ILogger<NoteRepository> logger)
    {
        _context = context;
        _logger = logger;
    }

    // ================= GET ALL =================
    public async Task<RepoResponse> GetAllNote(int clientId, int contactId)
    {
        try
        {
            var contact = await _context.contacts
                .FirstOrDefaultAsync(x => x.id == contactId);

            if (contact == null)
                return new RepoResponse { Success = false, Message = "Invalid contact id" };

            var notes = await _context.Notes
                .Where(x => x.ClientId == clientId && x.ContactId == contactId)
                .OrderByDescending(x => x.CreatedAt)
                .ToListAsync();

            return new RepoResponse
            {
                Success = true,
                Message = "Notes fetched successfully",
                Data = notes
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching notes");
            return new RepoResponse
            {
                Success = false,
                Message = "Something went wrong while fetching notes"
            };
        }
    }

    // ================= GET BY ID =================
    public async Task<RepoResponse> GetNoteById(int clientId, int contactId, int noteId)
    {
        try
        {
            var contact = await _context.contacts
                .FirstOrDefaultAsync(x => x.id == contactId);

            if (contact == null)
                return new RepoResponse { Success = false, Message = "Invalid contact id" };

            var note = await _context.Notes.FirstOrDefaultAsync(x =>
                x.ClientId == clientId &&
                x.ContactId == contactId &&
                x.Id == noteId);

            if (note == null)
                return new RepoResponse { Success = false, Message = "Note not found" };

            return new RepoResponse
            {
                Success = true,
                Message = "Note fetched successfully",
                Data = note
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching note");
            return new RepoResponse
            {
                Success = false,
                Message = "Something went wrong while fetching note"
            };
        }
    }

    // ================= ADD =================
    public async Task<RepoResponse> AddNote(NotesDto notes)
    {
        try
        {
            var contact = await _context.contacts
                .FirstOrDefaultAsync(x => x.id == notes.contactId);

            if (contact == null)
                return new RepoResponse { Success = false, Message = "Invalid contact id" };

            var note = new Notes
            {
                ClientId = notes.clientId,
                ContactId = notes.contactId,
                Note = notes.Note,
                IsPin = notes.IsPin,
                IsUseInGenration = notes.IsUseInGenration,
                CreatedAt = DateTime.UtcNow
            };

            await _context.Notes.AddAsync(note);
            await _context.SaveChangesAsync();

            return new RepoResponse
            {
                Success = true,
                Message = "Note added successfully"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding note");
            return new RepoResponse
            {
                Success = false,
                Message = "Something went wrong while adding note"
            };
        }
    }

    // ================= UPDATE =================
    public async Task<RepoResponse> UpdateNote(UpdateNotesDto update)
    {
        try
        {
            var contact = await _context.contacts
                .FirstOrDefaultAsync(x => x.id == update.contactId);

            if (contact == null)
                return new RepoResponse { Success = false, Message = "Invalid contact id" };

            var note = await _context.Notes.FirstOrDefaultAsync(x =>
                x.ClientId == update.clientId &&
                x.ContactId == update.contactId &&
                x.Id == update.NoteId);

            if (note == null)
                return new RepoResponse { Success = false, Message = "Note not found" };

            note.Note = update.Note;
            note.IsPin = update.IsPin;
            note.IsUseInGenration = update.IsUseInGenration;
            note.UpdatedAt = DateTime.UtcNow;

            _context.Notes.Update(note);
            await _context.SaveChangesAsync();

            return new RepoResponse
            {
                Success = true,
                Message = "Note updated successfully"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating note");
            return new RepoResponse
            {
                Success = false,
                Message = "Something went wrong while updating note"
            };
        }
    }

    // ================= DELETE =================
    public async Task<RepoResponse> DeleteNote(int clientId, int contactId, int noteId)
    {
        try
        {
            var contact = await _context.contacts
                .FirstOrDefaultAsync(x => x.id == contactId);

            if (contact == null)
                return new RepoResponse { Success = false, Message = "Invalid contact id" };

            var note = await _context.Notes.FirstOrDefaultAsync(x =>
                x.ClientId == clientId &&
                x.ContactId == contactId &&
                x.Id == noteId);

            if (note == null)
                return new RepoResponse { Success = false, Message = "Note not found" };

            _context.Notes.Remove(note);
            await _context.SaveChangesAsync();

            return new RepoResponse
            {
                Success = true,
                Message = "Note deleted successfully"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting note");
            return new RepoResponse
            {
                Success = false,
                Message = "Something went wrong while deleting note"
            };
        }
    }

    public class RepoResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public object? Data { get; set; }   // GET ke liye, ADD/UPDATE/DELETE me null
    }

}
