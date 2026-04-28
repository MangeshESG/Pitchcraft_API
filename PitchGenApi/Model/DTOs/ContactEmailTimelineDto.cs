namespace PitchGenApi.Model.DTOs
{
    public class ContactEmailTimelineDto
    {
        public int ContactId { get; set; }
        public string FullName { get; set; }
        public string Email { get; set; }
        public DateTime ContactCreatedAt { get; set; }

        public List<SentEmailDto> Emails { get; set; }
        public List<ContactNoteDto> Notes { get; set; }
        public List<ContactAttachmentDto> Attachments { get; set; }
    }

    public class EmailEventDto
    {
        public string EventType { get; set; }
        public DateTime EventAt { get; set; }
        public string TargetUrl { get; set; }
    }

    public class SentEmailDto
    {
        public string TrackingId { get; set; }
        public DateTime? SentAt { get; set; }
        public string SenderEmailId { get; set; }
        public string Subject { get; set; }
        public string Body { get; set; }
        public string Source { get; set; }

        public List<EmailEventDto> Events { get; set; }
        public List<EmailReplyDto> Replies { get; set; }   // ✅ add

    }

    public class ContactNoteDto
    {
        public int Id { get; set; }
        public string Note { get; set; }
        public DateTime CreatedAt { get; set; }
        public bool IsPin { get; set; }
        public bool IsUseInGenration { get; set; }
    }

}
public class ContactAttachmentDto
{
    public int Id { get; set; }
    public string FileName { get; set; }
    public string FileUrl { get; set; }
    public DateTime CreatedDate { get; set; }
}
public class EmailReplyDto
{
    public int Id { get; set; }
    public string MessageId { get; set; }
    public string InReplyTo { get; set; }
    public string FromEmail { get; set; }
    public string Subject { get; set; }
    public string Body { get; set; }
    public DateTime? Date { get; set; }
    public bool IsRead { get; set; }
}
