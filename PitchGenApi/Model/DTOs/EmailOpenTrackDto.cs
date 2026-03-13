using PitchGenApi.ValidationAttributes;

public class EmailOpenTrackDto
{
    [NoEncodedChars]
    public string TrackingId { get; set; }
}
