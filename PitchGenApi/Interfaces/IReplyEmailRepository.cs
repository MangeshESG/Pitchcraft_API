using PitchGenApi.Model.DTOs;

namespace PitchGenApi.Interfaces
{
    public interface IReplyEmailRepository
    {
        Task<EmailSendResult> ReplyEmailUsingSmtp(Guid trackingid, int clientId, string replyBody, int outboxId, string BccEmail = "");
        Task<EmailSendResult> ReplyEmailUsingGmailApi(Guid trackingid, int clientId, string replyBody, int outboxId, string BccEmail = "");
        Task<EmailSendResult> ReplyEmailUsingOutlookApi(Guid trackingid, int clientId, string replyBody, int outboxId, string BccEmail = "");
    }
}
