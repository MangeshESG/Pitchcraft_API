using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PitchGenApi.Database;
using System.Net;

[ApiController]
[Route("track")]
public class OpenTrackingController : ControllerBase
{
    private readonly AppDbContext _context;

    public OpenTrackingController(AppDbContext context)
    {
        _context = context;
    }

    [HttpGet("open")]
    public async Task<IActionResult> TrackOpen([FromQuery] EmailOpenTrackDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Email) || dto.ClientId == 0 || dto.TrackingId == Guid.Empty)
            return BadRequest("Missing required parameters.");

        // Helper method to decode URL-encoded values
        string Decode(string input) => string.IsNullOrWhiteSpace(input) ? "" : Uri.UnescapeDataString(input);

        var email = Decode(dto.Email);
        var fullName = Decode(dto.FullName);
        var location = Decode(dto.Location);
        var company = Decode(dto.Company);
        var jobTitle = Decode(dto.JobTitle);
        var linkedin = Decode(dto.linkedin_URL);
        var website = Decode(dto.website);
        var zohoView = Decode(dto.ZohoViewName);

        var alreadyExists = await _context.EmailTrackingLogs
            .AnyAsync(x => x.TrackingId == dto.TrackingId && x.EventType == "Open");

        if (!alreadyExists)
        {
            _context.EmailTrackingLogs.Add(new EmailTrackingLog
            {
                TrackingId = dto.TrackingId,
                Email = email,
                ContactId = dto.contactId,
                EventType = "Open",
                Timestamp = DateTime.UtcNow,
                ClientId = dto.ClientId,
                ZohoViewName = "from pitch craft",
                DataFileId = dto.DataFileId,
                Full_Name = fullName,
                Location = location,
                Company = company,
                JobTitle = jobTitle,
                linkedin_URL = linkedin,
                website = website
            });

            await _context.SaveChangesAsync();
        }

        byte[] pixelBytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR4nGNgYAAAAAMAASsJTYQAAAAASUVORK5CYII=");
        return File(pixelBytes, "image/png");
    }

    [HttpGet("click")]
    public async Task<IActionResult> TrackClick([FromQuery] EmailOpenTrackDto dto)
    {
        string Decode(string input) => string.IsNullOrWhiteSpace(input) ? "" : Uri.UnescapeDataString(input);

        if (string.IsNullOrWhiteSpace(dto.Email) ||
            string.IsNullOrWhiteSpace(dto.Url) ||
            dto.TrackingId == Guid.Empty ||
            dto.ClientId == 0 ||
            dto.DataFileId == 0)
        {
            return Redirect(dto.Url);
        }

        var userAgent = Request.Headers["User-Agent"].ToString()?.ToLower() ?? "";
        string browser = GetBrowserName(userAgent);

        var suspiciousAgents = new[] { "googleimageproxy", "thunderbird", "yahoo", "curl", "bot", "preview", "proxy" };

        bool isTrustedBrowser = userAgent.Contains("chrome") ||
                                userAgent.Contains("firefox") ||
                                userAgent.Contains("safari") ||
                                userAgent.Contains("edge");

        bool isSuspiciousAgent = suspiciousAgents.Any(agent => userAgent.Contains(agent));

        if (isSuspiciousAgent && !isTrustedBrowser)
        {
            // Log the suspicious click for analysis
            _context.EmailTrackingLogs.Add(new EmailTrackingLog
            {
                TrackingId = dto.TrackingId,
                ContactId = dto.contactId,
                Email = Decode(dto.Email),
                EventType = "Click",
                Timestamp = DateTime.UtcNow,
                ClientId = dto.ClientId,
                DataFileId = dto.DataFileId,
                ZohoViewName = "BOT_DETECTED",
                TargetUrl = Decode(dto.Url),
                Full_Name = Decode(dto.FullName),
                Location = Decode(dto.Location),
                Company = Decode(dto.Company),
                JobTitle = Decode(dto.JobTitle),
                linkedin_URL = Decode(dto.linkedin_URL),
                website = Decode(dto.website),
                UserAgent = userAgent,
                IPAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
                IsBot = true,
                Browser = browser
            });

            await _context.SaveChangesAsync();

            return Redirect(dto.Url);
        }


        if (dto.TrackingId == Guid.Empty)
        {
            return Redirect(dto.Url);
        }

        var sentEmail = await _context.EmailLogs
            .FirstOrDefaultAsync(e => e.TrackingId == dto.TrackingId);

        if (sentEmail == null)
            return Redirect(dto.Url);

        // ✅ Add this condition
        if (!string.Equals(sentEmail.ToEmail?.Trim(), Decode(dto.Email).Trim(), StringComparison.OrdinalIgnoreCase) ||
            sentEmail.DataFileId != dto.DataFileId)
        {
            return Redirect(dto.Url);
        }

        if (sentEmail.SentAt.HasValue)
        {
            var timeSinceSent = DateTime.UtcNow - sentEmail.SentAt.Value;
            if (timeSinceSent.TotalSeconds < 60)
            {
                return Redirect(dto.Url);
            }
        }

        bool alreadyClicked = await _context.EmailTrackingLogs.AnyAsync(x =>
            x.TrackingId == dto.TrackingId &&
            x.TargetUrl == dto.Url &&
            x.EventType == "Click");

        if (!alreadyClicked)
        {
            _context.EmailTrackingLogs.Add(new EmailTrackingLog
            {
                TrackingId = dto.TrackingId,
                ContactId = dto.contactId,
                Email = Decode(dto.Email),
                EventType = "Click",
                Timestamp = DateTime.UtcNow,
                ClientId = dto.ClientId,
                DataFileId = dto.DataFileId,
                ZohoViewName = "from pitch craft",
                TargetUrl = Decode(dto.Url),
                Full_Name = Decode(dto.FullName),
                Location = Decode(dto.Location),
                Company = Decode(dto.Company),
                JobTitle = Decode(dto.JobTitle),
                linkedin_URL = Decode(dto.linkedin_URL),
                website = Decode(dto.website),
                UserAgent = userAgent,
                IPAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
                IsBot = true,
                Browser = browser

            });

            await _context.SaveChangesAsync();
        }

        return Redirect(dto.Url);
    }


    [HttpGet("logs/by-client-viewid")]
    public async Task<IActionResult> GetEmailTrackingLogsByClient([FromQuery] int clientId, [FromQuery] string zohoViewName)
    {
        if (clientId <= 0)
            return BadRequest("ClientId is required and must be greater than 0.");

        // 1. Fetch Email Tracking Logs
        var logs = await _context.EmailTrackingLogs
            .Where(e => e.ClientId == clientId)
            .OrderByDescending(e => e.Timestamp)
            .Select(e => new
            {
                e.Id,
                e.Email,
                e.EventType,
                e.Timestamp,
                e.ClientId,
                TargetUrl = e.TargetUrl ?? "",
                ZohoViewName = e.ZohoViewName ?? "",
                FullName = e.Full_Name ?? "",
                Location = e.Location ?? "",
                Company = e.Company ?? "",
                JobTitle = e.JobTitle ?? "",
                linkedin_URL = e.linkedin_URL ?? "",
                website = e.website ?? "",
            })
            .ToListAsync();

        // 2. Calculate Success Count (if ZohoViewName is provided)
        int successCount = 0;
        if (!string.IsNullOrWhiteSpace(zohoViewName))
        {
            successCount = await _context.EmailLogs
                .Where(e => e.IsSuccess == true &&
                            e.ClientId == clientId &&
                            e.zohoViewName == zohoViewName)
                .CountAsync();
        }

        // 3. Return combined response
        return Ok(new
        {
            SuccessCount = successCount,
            Logs = logs
        });
    }

    [HttpGet("logs/by-client")]
    public async Task<IActionResult> GetEmailTrackingLogsByClient([FromQuery] int clientId)
    {
        if (clientId <= 0)
            return BadRequest("ClientId is required and must be greater than 0.");

        var logs = await _context.EmailTrackingLogs
            .Where(e => e.ClientId == clientId)
            .OrderByDescending(e => e.Timestamp)
            .Select(e => new
            {
                e.Id,
                e.Email,
                e.EventType,
                e.Timestamp,
                e.ClientId,
                TargetUrl = e.TargetUrl ?? "",
                ZohoViewName = e.ZohoViewName ?? "",
                FullName = e.Full_Name ?? "",
                Location = e.Location ?? "",
                Company = e.Company ?? "",
                JobTitle = e.JobTitle ?? "",
                linkedin_URL = e.linkedin_URL ?? "",
                website = e.website ?? "",
            })
            .ToListAsync();

        return Ok(logs);
    }

    [HttpGet("api/emaillogs/success-count")]
    public async Task<IActionResult> GetSuccessCount([FromQuery] string clientId, [FromQuery] string ZohoViewName)
    {
        if (!int.TryParse(clientId, out int parsedClientId))
            return BadRequest("Valid clientId and zohoViewName are required.");

        int count = await _context.EmailLogs
            .Where(e => e.IsSuccess == true &&
                        e.ClientId == parsedClientId &&
                        e.zohoViewName == ZohoViewName)

            .CountAsync();

        return Ok(count);
    }
    private string GetBrowserName(string userAgent)
    {
        userAgent = userAgent.ToLower();

        if (userAgent.Contains("edg/")) return "Edge";
        if (userAgent.Contains("chrome/") && !userAgent.Contains("edg/")) return "Chrome";
        if (userAgent.Contains("firefox/")) return "Firefox";
        if (userAgent.Contains("safari/") && !userAgent.Contains("chrome/")) return "Safari";
        if (userAgent.Contains("opera") || userAgent.Contains("opr/")) return "Opera";
        return "Unknown";
    }
}
