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

        // Bot detection for opens
        var userAgent = Request.Headers["User-Agent"].ToString()?.ToLower() ?? "";
        var suspiciousAgents = new[] {
            "googleimageproxy", "thunderbird", "yahoo", "bot", "crawler",
            "preview", "proxy", "scanner", "monitor"
        };

        bool isSuspiciousAgent = suspiciousAgents.Any(agent => userAgent.Contains(agent));
        if (isSuspiciousAgent)
        {
            // Return pixel but don't log
            byte[] botPixelBytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR4nGNgYAAAAAMAASsJTYQAAAAASUVORK5CYII=");
            return File(botPixelBytes, "image/png");
        }

        // Rest of your existing code...
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
                SegmentId = dto.SegmentId,
                Full_Name = fullName,
                Location = location,
                Company = company,
                JobTitle = jobTitle,
                linkedin_URL = linkedin,
                website = website,
                UserAgent = userAgent,
                IPAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
                IsBot = false
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
            (dto.DataFileId == 0 && (dto.SegmentId == null || dto.SegmentId == 0)))
        {
            return Redirect(dto.Url);
        }

        var userAgent = Request.Headers["User-Agent"].ToString()?.ToLower() ?? "";
        string browser = GetBrowserName(userAgent);

        // Enhanced bot detection patterns
        var suspiciousAgents = new[] {
            "googleimageproxy", "thunderbird", "yahoo", "curl", "bot", "preview", "proxy",
            "spider", "crawler", "scraper", "headless", "phantom", "selenium", "puppeteer",
            "fetch", "python", "java", "ruby", "perl", "wget", "scanner", "monitor"
        };

        // Trusted browser identifiers

        bool isTrustedBrowser = userAgent.Contains("chrome") ||

                                userAgent.Contains("firefox") ||

                                userAgent.Contains("safari") ||

                                userAgent.Contains("edge");

        // Check if User-Agent contains suspicious keywords

        bool isSuspiciousAgent = suspiciousAgents.Any(agent => userAgent.Contains(agent));

        // Additional bot detection checks
        bool hasNoReferer = string.IsNullOrEmpty(Request.Headers["Referer"].ToString());
        bool hasNoAcceptLanguage = string.IsNullOrEmpty(Request.Headers["Accept-Language"].ToString());
        bool isAutomatedPattern = userAgent.Length < 30 || userAgent.Contains("http://") || userAgent.Contains("https://");

        // Check if it's likely a bot
        if ((isSuspiciousAgent && !isTrustedBrowser) ||
            (hasNoReferer && hasNoAcceptLanguage) ||
            isAutomatedPattern ||
            string.IsNullOrWhiteSpace(userAgent))
        {
            // Don't log bot clicks as real clicks
            return Redirect(dto.Url);

        }

        // Check timing - if click happened too quickly after email sent
        var sentEmail = await _context.EmailLogs

            .FirstOrDefaultAsync(e => e.TrackingId == dto.TrackingId);

        if (sentEmail == null)
            return Redirect(dto.Url);

        // Check for rapid clicks (less than 5 seconds after sending)
        if (sentEmail.SentAt.HasValue)
        {
            var timeSinceSent = DateTime.UtcNow - sentEmail.SentAt.Value;
            if (timeSinceSent.TotalSeconds < 5)
            {
                // Too fast to be human
                return Redirect(dto.Url);
            }
        }

        // Verify email matches
        bool isEmailMatch = string.Equals(
            sentEmail.ToEmail?.Trim(),
            Decode(dto.Email).Trim(),
            StringComparison.OrdinalIgnoreCase);

        bool isFileMatch = sentEmail.DataFileId == dto.DataFileId;
        bool isSegmentMatch = sentEmail.SegmentId == dto.SegmentId;

        if (!(isEmailMatch && (isFileMatch || isSegmentMatch)))
        {
            return Redirect(dto.Url);
        }

        // Check for duplicate clicks - Fixed the bool? issue
        bool alreadyClicked = await _context.EmailTrackingLogs.AnyAsync(x =>

            x.TrackingId == dto.TrackingId &&

            x.TargetUrl == dto.Url &&
            x.EventType == "Click" &&
            x.IsBot.HasValue && !x.IsBot.Value);

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
                SegmentId = dto.SegmentId,
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
                IsBot = false,
                Browser = browser
            });

            await _context.SaveChangesAsync();

        }

        // Always redirect to the URL

        return Redirect(dto.Url);

    }

    [HttpGet("logs/by-client-viewid")]
    public async Task<IActionResult> GetEmailTrackingLogsByClient([FromQuery] int clientId, [FromQuery] string zohoViewName)
    {
        if (clientId <= 0)
            return BadRequest("ClientId is required and must be greater than 0.");

        // 1. Fetch Email Tracking Logs - excluding bot clicks
        var logs = await _context.EmailTrackingLogs
            .Where(e => e.ClientId == clientId && (!e.IsBot.HasValue || !e.IsBot.Value))
            .OrderByDescending(e => e.Timestamp)
            .Select(e => new
            {
                e.Id,
                e.Email,
                e.EventType,
                e.Timestamp,
                e.ClientId,
                e.ContactId,
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
            .Where(e => e.ClientId == clientId && (!e.IsBot.HasValue || !e.IsBot.Value))
            .OrderByDescending(e => e.Timestamp)
            .Select(e => new
            {
                e.Id,
                e.Email,
                e.EventType,
                e.Timestamp,
                e.ClientId,
                e.DataFileId,
                e.SegmentId,
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

    [HttpGet("tracking/segment")]
    public IActionResult GetSegmentTracking(int segmentId, int clientId)
    {
        var result = _context.EmailTrackingLogs
            .Where(x => x.SegmentId == segmentId && x.ClientId == clientId && (!x.IsBot.HasValue || !x.IsBot.Value))
            .ToList();

        if (result.Any())
            return Ok(result);
        else
            return NotFound("No data found for the given segmentId and clientId.");
    }

    [HttpGet("log/segment")]
    public IActionResult GetSegmntLog(int segmentId, int clientId)
    {
        var result = _context.EmailLogs
            .Where(x => x.SegmentId == segmentId && x.ClientId == clientId)
            .ToList();

        if (result.Any())
            return Ok(result);
        else
            return NotFound("No data found for the given segmentId and clientId.");
    }

    [HttpGet("missing-log-contacts")]
    public async Task<IActionResult> GetMissingLogContacts(
    [FromQuery] DateTime startDate,
    [FromQuery] DateTime endDate,
    [FromQuery] int dataFileId)
    {
        if (dataFileId <= 0)
            return BadRequest("dataFileId is required");

        // Force end date to full day 
        endDate = endDate.Date.AddDays(1).AddTicks(-1);

        // Get all contacts of data file
        var contacts = await _context.contacts
            .Where(c => c.DataFileId == dataFileId)
            .ToListAsync();

        // Get logs of this data file within date range
        var loggedContactIds = await _context.EmailLogs
            .Where(l => l.DataFileId == dataFileId
                    && l.SentAt >= startDate
                    && l.SentAt <= endDate)
            .Select(l => l.ContactId)
            .Distinct()
            .ToListAsync();

        // Filter missing contacts
        var missingContacts = contacts
            .Where(c => !loggedContactIds.Contains(c.id))
            .Select(c => new
            {
                c.full_name,
                c.email,
                c.company_name,
                c.job_title,
                c.country_or_address,
                c.email_subject,
                c.linkedin_url,
                c.website
            })
            .ToList();

        return Ok(new
        {
            missingContacts = missingContacts
        });
    }

    [HttpPost("delete-tracking-contact")]
    public async Task<IActionResult> DeleteContact([FromQuery] int contactId)
    {
        try
        {
            var contact = await _context.contacts
                .FirstOrDefaultAsync(c => c.id == contactId);

            if (contact == null)
            {
                return NotFound(new { message = "Contact not found." });
            }

            // Get all tracking logs for this contact
            var trackLogs = await _context.EmailTrackingLogs
                .Where(t => t.ContactId == contactId)
                .ToListAsync();

            // Get all email logs for this contact
            var emailLogs = await _context.EmailLogs
                .Where(e => e.ContactId == contactId)
                .ToListAsync();

            // Remove tracking logs
            if (trackLogs.Any())
                _context.EmailTrackingLogs.RemoveRange(trackLogs);

            // Remove email logs
            if (emailLogs.Any())
                _context.EmailLogs.RemoveRange(emailLogs);

            // Remove the main contact
            _context.contacts.Remove(contact);

            await _context.SaveChangesAsync();

            return Ok(new { message = "Contact and related logs deleted successfully." });
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }


    private string GetBrowserName(string userAgent)
    {
        userAgent = userAgent.ToLower();

        if (string.IsNullOrWhiteSpace(userAgent) || userAgent.Length < 30)
            return "Bot/Unknown";

        if (userAgent.Contains("bot") || userAgent.Contains("crawler") || userAgent.Contains("spider"))
            return "Bot";
        if (userAgent.Contains("edg/")) return "Edge";
        if (userAgent.Contains("chrome/") && !userAgent.Contains("edg/")) return "Chrome";
        if (userAgent.Contains("firefox/")) return "Firefox";
        if (userAgent.Contains("safari/") && !userAgent.Contains("chrome/")) return "Safari";
        if (userAgent.Contains("opera") || userAgent.Contains("opr/")) return "Opera";
        return "Unknown";
    }
}