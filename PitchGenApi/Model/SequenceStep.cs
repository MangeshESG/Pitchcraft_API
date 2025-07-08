
using PitchGenApi.Model;

public class SequenceStep
{
    public int Id { get; set; }
    public int ClientId { get; set; }
    public string Title { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool TestIsSent  { get; set; }

    // Separate columns for date and time
    public DateTime ScheduledDate { get; set; }      // Store date part
    public TimeSpan ScheduledTime { get; set; }      // Store time part

    public string TimeZone { get; set; }
    public string zohoviewName { get; set; }
    public int SmtpID { get; set; }
    public string? BccEmail { get; set; }
    public bool IsSent { get; set; }

}
