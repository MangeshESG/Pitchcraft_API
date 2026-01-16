using Microsoft.AspNetCore.Mvc;
using PitchGenApi.Model;
using PitchGenApi.Model.DTOs;

namespace PitchGenApi.Interfaces
{
    public interface IDomainVerificationRepository
    {
        Task<OperationResult> GenerateToken(string email, int clientId, string smtpdetails, string ip, string browsername);
        Task<OperationResult> VerifyDomain(string domain, int clientId);
        Task<bool> IsSmtpFullyVerifiedAsync(int smtpId);
        //Task<OperationResult> DomainVerifyEmailOTP(string email, string otp, int clientId);
        Task<OperationResult> GetVerifiedDomain(int clientId);
        Task<OperationResult> VerifySpfDkimDmarc(string email, string clientId);
        Task<OperationResult> VerifySmtpOtp(string email, string otp, string clientId);
        Task<bool> CustomDKIM(string domain, string selector, string expectedDkimValue, int clientId);
        Task<bool> CustomDMARC(string domain, string dmarcPrefix, string expectedDmarcValue, int clientId);
    }
}
