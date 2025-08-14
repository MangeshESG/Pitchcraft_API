namespace PitchGenApi.Model
{
    public class TempRegisterData
    {
        public int Id { get; set; }
        public string Email { get; set; }
        public string JsonData { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime ExpiresAt { get; set; }
    }

}
