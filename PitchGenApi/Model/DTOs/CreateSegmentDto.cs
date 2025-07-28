namespace PitchGenApi.Model.DTOs
{
    public class CreateSegmentDto
    {
        public string Name { get; set; } = default!;
        public string? Description { get; set; }
        public int? DataFileId { get; set; }
        public List<int> ContactIds { get; set; } = new();
    }

}
