namespace PitchGenApi.Model.DTOs
{
    public class LoginRequestDTO
    {
        public string? username { get; set; }
        public string password { get; set; }
        public int? trustednumber { get; set; }
    }
}
