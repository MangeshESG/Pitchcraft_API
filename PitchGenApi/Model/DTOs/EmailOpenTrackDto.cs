using PitchGenApi.ValidationAttributes;

public class EmailOpenTrackDto
{
    [NoEncodedChars]
    public string Email { get; set; }

    [NoEncodedChars]
    public Guid TrackingId { get; set; }

    [NoEncodedChars]
    public int ClientId { get; set; }

    [NoEncodedChars]
    public string ZohoViewName { get; set; }

    [NoEncodedChars]
    public string? FullName { get; set; }

    [NoEncodedChars]
    public string? Location { get; set; }

    [NoEncodedChars]
    public string? Company { get; set; }

    [NoEncodedChars]
    public string? Url { get; set; }

    [NoEncodedChars]
    public string? JobTitle { get; set; }

    [NoEncodedChars]
    public string? linkedin_URL { get; set; }

    [NoEncodedChars]
    public string? website { get; set; }
}
