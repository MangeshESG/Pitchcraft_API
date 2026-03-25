namespace PitchGenApi.Model.DTOs
{
    public class CreateViewDto
    {
        public int ClientId { get; set; }

        public string Name { get; set; }

        public string Description { get; set; }

        public List<int>? DataFileIds { get; set; }

        public List<int>? SegmentIds { get; set; }

        public string? FiltersJson { get; set; }

        public bool UseAllDataFiles { get; set; } // NEW
        public List<int>? ExcludedDataFileIds { get; set; } // optional
    }
}
