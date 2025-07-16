using System.ComponentModel.DataAnnotations;

public class SequenceCreateDto
{
    [Required]
    public string Title { get; set; }
   
    [Required]
    public string zohoviewName { get; set; } //for zohoviwe
    
    public bool TestIsSent { get; set; }

    [Required]
    public int SmtpID { get; set; }

    [Required]
    public string TimeZone { get; set; }

    public string? BccEmail { get; set; }

    [Required]
    public List<StepDto> Steps { get; set; }
    public int? DataFileId { get; set; }


    public class StepDto
    {
        public DateTime ScheduledDate { get; set; } // e.g. "2025-05-06"
        public TimeSpan ScheduledTime { get; set; } // e.g. "10:30:00"
        
    }
}
