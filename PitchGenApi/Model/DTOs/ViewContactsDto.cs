namespace PitchGenApi.Model.DTOs
{
    public class ViewContactsRequest
    {
        public int ClientId { get; set; }
        public int ViewId { get; set; }
        public int Page { get; set; } = 1;
        public int PageSize { get; set; } = 100;
        public string? Search { get; set; }
    }

    public class FiltersPayload
    {
        public string? Logic { get; set; }
        public List<FilterGroupDto>? Groups { get; set; }
        public List<FilterConditionDto>? Conditions { get; set; }
    }

    public class FilterGroupDto
    {
        public string? JoinWithPrevious { get; set; }
        public List<FilterConditionDto>? Conditions { get; set; }
    }

    public class FilterConditionContextDto
    {
        public int? CampaignId { get; set; }
        public string? CampaignName { get; set; }
    }

    public class FilterConditionDto
    {
        public string? Field { get; set; }
        public string? Operator { get; set; }
        public string? Value { get; set; }
        public string? JoinWithPrevious { get; set; }
        public FilterConditionContextDto? Context { get; set; }
    }
}
