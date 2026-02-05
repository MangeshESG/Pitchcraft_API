using Microsoft.AspNetCore.Mvc;
using PitchGenApi.Interfaces;
using PitchGenApi.Model.DTOs;

namespace PitchGenApi.Controllers
{
    [ApiController]
    [Route("api/notes")]
    public class NotesController : ControllerBase
    {
        private readonly INoteRepository _repo;
        private readonly ILogger<NotesController> _logger;

        public NotesController(INoteRepository repo, ILogger<NotesController> logger)
        {
            _repo = repo;
            _logger = logger;
        }

        // ================= GET ALL =================
        [HttpGet("Get-All-Note")]
        public async Task<IActionResult> GetAll(
            [FromQuery] int clientId,
            [FromQuery] int contactId)
        {
            try
            {
                var notes = await _repo.GetAllNote(clientId, contactId);
                return Ok(notes);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Controller error - GetAll");
                return StatusCode(500, "Internal server error");
            }
        }

        // ================= GET ONE =================
        [HttpGet("Get-Note-By-Id")]
        public async Task<IActionResult> GetById(
            [FromQuery] int clientId,
            [FromQuery] int contactId,
            [FromQuery] int noteId)
        {
            try
            {
                var note = await _repo.GetNoteById(clientId, contactId, noteId);

                if (note == null)
                    return NotFound("Note not found");

                return Ok(note);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Controller error - GetById");
                return StatusCode(500, "Internal server error");
            }
        }

        // ================= ADD =================
        [HttpPost("Add-Note")]
        public async Task<IActionResult> Add([FromBody] NotesDto notes)
        {
            try
            {
                var result = await _repo.AddNote(notes);

                if (!result.Success)
                    return BadRequest(result.Message);

                return Ok(result.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Controller error - Add");
                return StatusCode(500, "Internal server error");
            }
        }

        // ================= UPDATE =================
        [HttpPost("Update-Note")]
        public async Task<IActionResult> Update([FromQuery] UpdateNotesDto update )
        {
            try
            {
                var result = await _repo.UpdateNote(update);

                if (!result.Success)
                    return BadRequest(result.Message);

                return Ok(result.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Controller error - Update");
                return StatusCode(500, "Internal server error");
            }
        }

        // ================= DELETE =================
        [HttpPost("Delete-Note")]
        public async Task<IActionResult> Delete(
            [FromQuery] int clientId,
            [FromQuery] int contactId,
            [FromQuery] int noteId)
        {
            try
            {
                var result = await _repo.DeleteNote(clientId, contactId, noteId);

                if (!result.Success)
                    return BadRequest(result.Message);

                return Ok(result.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Controller error - Delete");
                return StatusCode(500, "Internal server error");
            }
        }
    }
}
