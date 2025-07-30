public class EmailTrackingLog
{
    public int Id { get; set; }
    public int ContactId { get; set; }
    public Guid TrackingId { get; set; }
    public string Email { get; set; }
    public string EventType { get; set; }
    public DateTime Timestamp { get; set; }
    public int ClientId { get; set; }
    public string? TargetUrl { get; set; }
    public string? ZohoViewName { get; set; }
    public int? DataFileId { get; set; }

    public string? Full_Name { get; set; }
    public string? Location { get; set; }
    public string? Company { get; set; }
    public string? JobTitle { get; set; }
    public string? linkedin_URL { get; set; }
    public string? website { get; set; }
    public string? UserAgent { get; set; }     // ✅ new
    public bool IsBot { get; set; }            // ✅ new
    public string? IPAddress { get; set; }     // ✅ new
    public string? Browser { get; set; }
}