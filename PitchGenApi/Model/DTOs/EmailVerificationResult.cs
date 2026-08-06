namespace PitchGenApi.Model.DTOs
{
    public enum EmailVerificationState
    {
        Valid,
        Invalid,
        VerificationUnavailable
    }

    public sealed record EmailVerificationResult(
        EmailVerificationState State,
        string Status);
}
