namespace PitchGenApi.Model
{
    public class EmailOtpVerification
    {
        public int Id { get; set; }
        public string Email { get; set; }
        public string? username { get; set; }

        public string OTP { get; set; }
        public bool IsVerified { get; set; }
        public string OtpType { get; set; }
        public string? TempSmtpPayload { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime ExpiresAt { get; set; }
    }
}
