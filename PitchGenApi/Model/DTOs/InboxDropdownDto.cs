namespace PitchGenApi.Model.DTOs
{
    public class InboxDropdownDto
    {
        public int InboxId { get; set; }

        public string EmailAddress { get; set; }

        public string Provider { get; set; }

        // InboxEmails unread
        public int InboxEmailsUnreadCount { get; set; }

        // EmailReplies unread
        public int EmailRepliesUnreadCount { get; set; }

        // Total unread
        public int TotalUnreadCount { get; set; }
    }
}