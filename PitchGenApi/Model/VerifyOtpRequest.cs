namespace PitchGenApi.Model
{
    public class VerifyOtpRequest
    {
        public string Email { get; set; }
        public string Otp { get; set; }
        public bool trustthisdivice { get; set; }
    }
}
