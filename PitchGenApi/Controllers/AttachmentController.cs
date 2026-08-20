using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
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
        private readonly AppDbContext _context;

        public AttachmentController(IAttachmentRepository repository, AppDbContext context)
        {
            _repository = repository;
            _context = context;
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

        [HttpGet("profile-image/{contactId:int}")]
        public async Task<IActionResult> GetProfileImage(int contactId, [FromQuery] int clientId)
        {
            var ownsContact = await _context.contacts.AnyAsync(
                x => x.id == contactId &&
                     x.data_file != null &&
                     x.data_file.client_id == clientId);
            if (!ownsContact)
                return NotFound();

            const string profileImageMarker = "__CONTACT_PROFILE_IMAGE__";
            var image = await _context.ContactAttachments
                .Where(x => x.ContactId == contactId && x.Description == profileImageMarker)
                .OrderByDescending(x => x.CreatedDate)
                .FirstOrDefaultAsync();

            if (image == null)
                return NotFound();

            var path = Path.Combine(Directory.GetCurrentDirectory(), image.FileUrl.TrimStart('/'));
            if (!System.IO.File.Exists(path))
                return NotFound("File not found");

            var provider = new FileExtensionContentTypeProvider();
            if (!provider.TryGetContentType(path, out string? contentType) ||
                !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                return BadRequest("Profile attachment is not an image");

            Response.Headers.CacheControl = "no-store, no-cache";
            return PhysicalFile(path, contentType);
        }

        [HttpPost("profile-image/update")]
        [RequestSizeLimit(10485760)]
        public async Task<IActionResult> UpdateProfileImage([FromForm] UploadAttachmentRequest request)
        {
            var ownsContact = await _context.contacts.AnyAsync(
                x => x.id == request.ContactId &&
                     x.data_file != null &&
                     x.data_file.client_id == request.ClientId);
            if (!ownsContact)
                return NotFound("Contact not found");

            if (request.File == null || request.File.Length == 0)
                return BadRequest("Image required");

            if (request.File.Length > 10485760)
                return BadRequest("Image exceeds 10MB");

            var provider = new FileExtensionContentTypeProvider();
            if (!provider.TryGetContentType(request.File.FileName, out var contentType) ||
                !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                return BadRequest("Only image files are allowed");

            var contactFolder = Path.Combine(
                Directory.GetCurrentDirectory(),
                "ContactAttachments",
                $"Contact_{request.ContactId}");
            Directory.CreateDirectory(contactFolder);

            var extension = Path.GetExtension(request.File.FileName);
            var uniqueName = $"{Guid.NewGuid()}{extension}";
            var filePath = Path.Combine(contactFolder, uniqueName);

            await using (var stream = new FileStream(filePath, FileMode.Create))
                await request.File.CopyToAsync(stream);

            var attachment = new ContactAttachments
            {
                ContactId = request.ContactId,
                FileName = $"Profile image{extension}",
                Description = "__CONTACT_PROFILE_IMAGE__",
                FileUrl = $"/ContactAttachments/Contact_{request.ContactId}/{uniqueName}",
                FileSize = request.File.Length,
                CreatedDate = DateTime.UtcNow
            };

            var result = await _repository.AddAttachment(attachment);
            return Ok(result);
        }

        [HttpPost("profile-image/delete")]
        public async Task<IActionResult> DeleteProfileImage([FromBody] DeleteProfileImageRequest request)
        {
            var ownsContact = await _context.contacts.AnyAsync(
                x => x.id == request.ContactId &&
                     x.data_file != null &&
                     x.data_file.client_id == request.ClientId);
            if (!ownsContact)
                return NotFound("Contact not found");

            var images = await _context.ContactAttachments
                .Where(x => x.ContactId == request.ContactId &&
                            x.Description == "__CONTACT_PROFILE_IMAGE__")
                .ToListAsync();

            foreach (var image in images)
            {
                var path = Path.Combine(Directory.GetCurrentDirectory(), image.FileUrl.TrimStart('/'));
                if (System.IO.File.Exists(path))
                    System.IO.File.Delete(path);
            }

            _context.ContactAttachments.RemoveRange(images);
            await _context.SaveChangesAsync();
            return Ok(new { message = "Profile image deleted" });
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

    public class DeleteProfileImageRequest
    {
        public int ContactId { get; set; }
        public int ClientId { get; set; }
    }
}
