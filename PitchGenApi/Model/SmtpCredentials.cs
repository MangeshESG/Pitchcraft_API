namespace PitchGenApi.Model
{
    public class SmtpCredentials
    {
        public int Id { get; set; }
        public string ClientId { get; set; }
        public string Server { get; set; }
        public int Port { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
        public string FromEmail { get; set; }
        public bool UseSsl { get; set; }
    }
}
