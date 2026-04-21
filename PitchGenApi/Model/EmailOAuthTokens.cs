namespace PitchGenApi.Model
{
    public class EmailOAuthTokens
    {
        public int Id { get; set; }
        public int ClientId { get; set; }
        public string Email { get; set; }
        public string Provider { get; set; }

        public string AccessToken { get; set; }
        public string RefreshToken { get; set; }
        public string SenderName { get; set; }
        public DateTime ExpiryTime { get; set; }
        public DateTime? LastInboxSyncAt { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
