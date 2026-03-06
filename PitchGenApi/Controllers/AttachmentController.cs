using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PitchGenApi.Database;
using PitchGenApi.Interfaces;
using PitchGenApi.Model;
using PitchGenApi.Model.DTOs;

namespace PitchGenApi.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class AttachmentController : ControllerBase
    {
        private readonly IAttachmentRepository _repository;

        public AttachmentController(IAttachmentRepository repository)
        {
            _repository = repository;
        }

        [HttpPost("upload")]
        [RequestSizeLimit(52428800)] // 50MB
        public async Task<IActionResult> Upload([FromForm] UploadAttachmentRequest request)
        {
            if (request.File == null)
                return BadRequest("File required");

            if (request.File.Length > 52428800)
                return BadRequest("File exceeds 50MB");

            var folder = Path.Combine(Directory.GetCurrentDirectory(), "ContactAttachments");

            if (!Directory.Exists(folder))
                Directory.CreateDirectory(folder);

            var uniqueName = Guid.NewGuid() + Path.GetExtension(request.File.FileName);

            var filePath = Path.Combine(folder, uniqueName);

            using (var stream = new FileStream(filePath, FileMode.Create))
            {
                await request.File.CopyToAsync(stream);
            }

            var attachment = new ContactAttachments
            {
                ContactId = request.ContactId,
                FileName = request.Name,
                Description = request.Description,
                FileUrl = "/ContactAttachments/" + uniqueName,
                FileSize = request.File.Length,
                CreatedDate = DateTime.UtcNow
            };

            var result = await _repository.AddAttachment(attachment);

            return Ok(result);
        }
    }
}
