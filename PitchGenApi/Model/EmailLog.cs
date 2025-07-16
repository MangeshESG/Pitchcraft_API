public class EmailLog
{
    public int Id { get; set; }

    public int StepId { get; set; } // SequenceStep ka reference

    public string ToEmail { get; set; }

    public string Subject { get; set; }

    public string Body { get; set; }

    public bool IsSuccess { get; set; }

    public string? ErrorMessage { get; set; }
    public string? zohoViewName { get; set; }
    public int? DataFileId { get; set; }


    public DateTime? SentAt { get; set; }

    public int ClientId { get; set; }
    public Guid TrackingId { get; set; }
    public string? process_name { get; set; }

}
