using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
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
            if (request.File == null || request.File.Length == 0)
                return BadRequest("File required");

            if (request.File.Length > 52428800)
                return BadRequest("File exceeds 50MB");

            // Root folder
            var rootFolder = Path.Combine(Directory.GetCurrentDirectory(), "ContactAttachments");

            if (!Directory.Exists(rootFolder))
                Directory.CreateDirectory(rootFolder);

            // Contact specific folder
            var contactFolder = Path.Combine(rootFolder, $"Contact_{request.ContactId}");

            if (!Directory.Exists(contactFolder))
                Directory.CreateDirectory(contactFolder);

            var extension = Path.GetExtension(request.File.FileName);

            var uniqueName = Guid.NewGuid().ToString() + extension;

            var filePath = Path.Combine(contactFolder, uniqueName);

            using (var stream = new FileStream(filePath, FileMode.Create))
            {
                await request.File.CopyToAsync(stream);
            }

            var attachment = new ContactAttachments
            {
                ContactId = request.ContactId,
                FileName = request.Name + extension,
                Description = request.Description,
                FileUrl = $"/ContactAttachments/Contact_{request.ContactId}/{uniqueName}",
                FileSize = request.File.Length,
                CreatedDate = DateTime.UtcNow
            };

            var result = await _repository.AddAttachment(attachment);

            return Ok(result);
        }

        [HttpGet("download/{id}")]
        public async Task<IActionResult> Download(int id)
        {
            var file = await _repository.GetById(id);

            if (file == null)
                return NotFound();

            var path = Path.Combine(Directory.GetCurrentDirectory(), file.FileUrl.TrimStart('/'));

            if (!System.IO.File.Exists(path))
                return NotFound("File not found");

            var provider = new FileExtensionContentTypeProvider();

            if (!provider.TryGetContentType(path, out string contentType))
            {
                contentType = "application/octet-stream";
            }

            return PhysicalFile(path, contentType, file.FileName);
        }
    }
}
