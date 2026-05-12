namespace PitchGenApi.Model.DTOs
{
    public class PagedInboxEmailDto
    {
        public int TotalCount { get; set; }
        public int TotalPages { get; set; }
        public int PageNumber { get; set; }
        public int PageSize { get; set; }

        public List<EmailThreadDto> Data { get; set; }
    }
}
