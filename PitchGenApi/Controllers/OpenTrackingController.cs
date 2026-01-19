using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PitchGenApi.Database;
using System.Text;

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
        // --- Helper Functions ---
        string DecodeB64(string encoded, out bool success)
        {
            success = false;
            if (string.IsNullOrWhiteSpace(encoded)) return "";
            try
            {
                byte[] bytes = Convert.FromBase64String(encoded);
                success = true;
                return Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                return encoded; // fallback if not Base64
            }
        }

        int DecodeInt(string encoded, out bool success)
        {
            var decoded = DecodeB64(encoded, out success);
            return int.TryParse(decoded, out var val) ? val : 0;
        }

        Guid DecodeGuid(string encoded, out bool success)
        {
            var decoded = DecodeB64(encoded, out success);
            return Guid.TryParse(decoded, out var val) ? val : Guid.Empty;
        }

        // Decode all fields with Base64 check
        bool emailOk, clientOk, segmentOk, dataFileOk, contactOk, trackingOk;
        bool fullNameOk, locationOk, companyOk, jobTitleOk, linkedinOk, websiteOk;

        var email = DecodeB64(dto.Email, out emailOk);
        var clientId = DecodeInt(dto.ClientId, out clientOk);
        var segmentId = DecodeInt(dto.SegmentId, out segmentOk);
        var dataFileId = DecodeInt(dto.DataFileId, out dataFileOk);
        var contactId = DecodeInt(dto.contactId, out contactOk);
        var trackingId = DecodeGuid(dto.TrackingId, out trackingOk);

        var fullName = DecodeB64(dto.FullName, out fullNameOk);
        var location = DecodeB64(dto.Location, out locationOk);
        var company = DecodeB64(dto.Company, out companyOk);
        var jobTitle = DecodeB64(dto.JobTitle, out jobTitleOk);
        var linkedin = DecodeB64(dto.linkedin_URL, out linkedinOk);
        var website = DecodeB64(dto.website, out websiteOk);

        // If any field failed Base64 decoding → return pixel directly
        if (!emailOk || !clientOk || !segmentOk || !dataFileOk || !contactOk || !trackingOk ||
            !fullNameOk || !locationOk || !companyOk || !jobTitleOk || !linkedinOk || !websiteOk)
        {
            byte[] pixelBytesFallback = Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR4nGNgYAAAAAMAASsJTYQAAAAASUVORK5CYII=");
            return File(pixelBytesFallback, "image/png");
        }

        // Bot detection
        var userAgent = Request.Headers["User-Agent"].ToString()?.ToLower() ?? "";
        var suspiciousAgents = new[] {"thunderbird", "bot", "crawler", "preview", "proxy", "scanner", "monitor" };
        if (suspiciousAgents.Any(agent => userAgent.Contains(agent)))
        {
            byte[] botPixelBytes = Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR4nGNgYAAAAAMAASsJTYQAAAAASUVORK5CYII=");
            return File(botPixelBytes, "image/png");
        }

        // Prevent duplicate logs
        var alreadyExists = await _context.EmailTrackingLogs
            .AnyAsync(x => x.TrackingId == trackingId && x.EventType == "Open");

        if (!alreadyExists)
        {
            _context.EmailTrackingLogs.Add(new EmailTrackingLog
            {
                TrackingId = trackingId,
                Email = email,
                ContactId = contactId,
                EventType = "Open",
                Timestamp = DateTime.UtcNow,
                ClientId = clientId,
                DataFileId = dataFileId,
                SegmentId = segmentId,
                Full_Name = fullName,
                Location = location,
                Company = company,
                JobTitle = jobTitle,
                linkedin_URL = linkedin,
                website = website,
                ZohoViewName = "from pitch craft",
                UserAgent = userAgent,
                IPAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
                IsBot = false
            });

            await _context.SaveChangesAsync();
        }

        // Return 1px transparent PNG
        byte[] pixelBytes = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR4nGNgYAAAAAMAASsJTYQAAAAASUVORK5CYII=");
        return File(pixelBytes, "image/png");
    }



    [HttpGet("click")]
    public async Task<IActionResult> TrackClick([FromQuery] EmailOpenTrackDto dto)
    {
        // ============================================
        // Helper: Decode Base64 → string
        // ============================================
        string DecodeB64(string input, out bool ok)
        {
            ok = false;
            if (string.IsNullOrWhiteSpace(input)) return null;

            try
            {
                byte[] bytes = Convert.FromBase64String(input);
                ok = true;
                return Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                return null; // invalid base64
            }
        }

        // ============================================
        // Helper: Decode Base64 → int
        // ============================================
        int DecodeInt(string input, out bool ok)
        {
            ok = false;
            var decoded = DecodeB64(input, out var baseOk);
            if (!baseOk) return 0;

            ok = int.TryParse(decoded, out var val);
            return val;
        }

        // ============================================
        // Helper: Decode Base64 → Guid
        // ============================================
        Guid DecodeGuid(string input, out bool ok)
        {
            ok = false;
            var decoded = DecodeB64(input, out var baseOk);
            if (!baseOk) return Guid.Empty;

            ok = Guid.TryParse(decoded, out var val);
            return val;
        }

        // ============================================
        // Decode required base64 fields
        // ============================================
        var email = DecodeB64(dto.Email, out var emailOk);
        var url = DecodeB64(dto.Url, out var urlOk);

        var clientId = DecodeInt(dto.ClientId, out var clientOk);
        var contactId = DecodeInt(dto.contactId, out var contactOk);
        var segmentId = DecodeInt(dto.SegmentId, out var segmentOk);
        var dataFileId = DecodeInt(dto.DataFileId, out var dfOk);

        var trackingId = DecodeGuid(dto.TrackingId, out var trackingOk);

        var fullName = DecodeB64(dto.FullName, out var fullNameOk);
        var location = DecodeB64(dto.Location, out var locationOk);
        var company = DecodeB64(dto.Company, out var companyOk);
        var jobTitle = DecodeB64(dto.JobTitle, out var jobTitleOk);
        var linkedin = DecodeB64(dto.linkedin_URL, out var linkedinOk);
        var website = DecodeB64(dto.website, out var websiteOk);

        // ============================================
        // Base64 decoding failed → skip tracking
        // ============================================
        if (!emailOk || !urlOk || !clientOk || !contactOk || !segmentOk || !dfOk ||
            !trackingOk || !fullNameOk || !locationOk || !companyOk ||
            !jobTitleOk || !linkedinOk || !websiteOk)
        {
            return Redirect(url); // fallback
        }

        // ============================================
        // Basic required validation
        // ============================================
        if (string.IsNullOrWhiteSpace(email) ||
            string.IsNullOrWhiteSpace(url) ||
            trackingId == Guid.Empty ||
            clientId == 0 ||
            (segmentId == 0 && dataFileId == 0))
        {
            return Redirect(url);
        }

        // ============================================
        // Browser Detection
        // ============================================
        var userAgent = Request.Headers["User-Agent"].ToString()?.ToLower() ?? "";
        string browser = GetBrowserName(userAgent);

        var suspiciousAgents = new[]
        {
        "curl","bot","preview","proxy","spider","crawler",
        "scraper","headless","phantom","selenium","puppeteer",
        "wget","fetch","scanner","monitor","python","java","ruby","perl"
    };

        bool isTrustedBrowser = userAgent.Contains("chrome") ||
                                userAgent.Contains("firefox") ||
                                userAgent.Contains("safari") ||
                                userAgent.Contains("edge");

        bool suspicious = suspiciousAgents.Any(a => userAgent.Contains(a));
        bool isAutomatedPattern = userAgent.Length < 30 ||
                                  userAgent.Contains("http://") ||
                                  userAgent.Contains("https://");

        bool noLang = string.IsNullOrEmpty(Request.Headers["Accept-Language"].ToString());

        if ((suspicious && !isTrustedBrowser) || noLang || isAutomatedPattern)
        {
            return Redirect(url);
        }

        // ============================================
        // Check EmailLog for timing validation
        // ============================================
        var sentEmail = await _context.EmailLogs.FirstOrDefaultAsync(x => x.TrackingId == trackingId);
        if (sentEmail == null) return Redirect(url);

        if (sentEmail.SentAt.HasValue)
        {
            var seconds = (DateTime.UtcNow - sentEmail.SentAt.Value).TotalSeconds;
            if (seconds < 5) return Redirect(url); // too fast → bot
        }

        // ============================================
        // Email + File/Segment match
        // ============================================
        bool emailMatch = string.Equals(sentEmail.ToEmail?.Trim(), email.Trim(),
                                      StringComparison.OrdinalIgnoreCase);
        bool fileMatch = sentEmail.DataFileId == dataFileId;
        bool segMatch = sentEmail.SegmentId == segmentId;

        if (!(emailMatch && (fileMatch || segMatch))) return Redirect(url);

        // ============================================
        // Prevent duplicate tracking logs
        // ============================================
        bool alreadyClicked = await _context.EmailTrackingLogs.AnyAsync(x =>
            x.TrackingId == trackingId &&
            x.TargetUrl == url &&
            x.EventType == "Click" &&
            x.IsBot == false
        );
        bool hasOpenLog = await _context.EmailTrackingLogs.AnyAsync(x =>
            x.TrackingId == trackingId &&
            x.EventType == "Open" &&
            x.IsBot == false
        );

        if (!hasOpenLog)
        {
            // ❌ Open nahi hua → click track nahi karna
            return Redirect(url);
        }
        if (!alreadyClicked)
        {
            _context.EmailTrackingLogs.Add(new EmailTrackingLog
            {
                TrackingId = trackingId,
                ContactId = contactId,
                EventType = "Click",
                TargetUrl = url,
                ClientId = clientId,
                SegmentId = segmentId,
                DataFileId = dataFileId,
                Email = email,
                Full_Name = fullName,
                Location = location,
                Company = company,
                JobTitle = jobTitle,
                linkedin_URL = linkedin,
                website = website,
                UserAgent = userAgent,
                Browser = browser,
                IPAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
                Timestamp = DateTime.UtcNow,
                ZohoViewName = "from pitch craft",
                IsBot = false
            });

            await _context.SaveChangesAsync();
        }

        return Redirect(url);
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
                contactId = c.id,
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