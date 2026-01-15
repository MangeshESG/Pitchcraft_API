namespace PitchGenApi.Model.DTOs
{
    public class OperationResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = "";
        public string Domain { get; set; }
        public string Token { get; set; }
        public object? Data { get; set; }

        public bool DomainAlreadyVerified { get; set; } = false;
    }

}
