using PitchGenApi.Model.DTOs;

namespace PitchGenApi.Interfaces
{
    public interface IForwardRepository
    {
       Task<EmailSendResult> ForwardEmailUsingSmtp(Guid trackingid, int clientId, string forwardToEmail, string forwardMessage, int outboxId, string? BccEmail = "");
       Task<EmailSendResult> ForwardEmailUsingOutlookApi(Guid trackingid, int clientId, string forwardToEmail, string forwardMessage, int outboxId, string? BccEmail = "");
       Task<EmailSendResult> ForwardEmailUsingGmailApi(Guid trackingid, int clientId, string forwardToEmail, string forwardMessage, int outboxId, string? BccEmail = "");
    }
}


