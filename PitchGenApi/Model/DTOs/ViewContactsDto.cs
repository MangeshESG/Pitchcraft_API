namespace PitchGenApi.Model.DTOs
{
    public class ViewContactsRequest
    {
        public int ClientId { get; set; }
        public int ViewId { get; set; }
        public int Page { get; set; } = 1;
        public int PageSize { get; set; } = 100;
        public string? Search { get; set; }
        public string? SortBy { get; set; }
        public string? SortDirection { get; set; }

        /// <summary>
        /// A filter the browser has built but not saved yet, sent by "Apply
        /// filters" in the view editor. When set it stands in for the view's
        /// saved filters_json, so the preview is filtered over the whole view
        /// here rather than over the single page the browser is holding.
        /// </summary>
        public string? FiltersJson { get; set; }

        public bool IsFollowUp { get; set; }
        public bool NotKrafted { get; set; }
        public bool KraftedNotSent { get; set; }
        public bool IncludeEmailContent { get; set; } = true;
    }

    /// <summary>
    /// The filter panel's conditions for a list or segment, carried in the
    /// request body.
    ///
    /// They used to ride in the query string, which the live server's firewall
    /// rejects outright (403) once the URL carries JSON. A saved view already
    /// posts the same payload to view-contacts and passes, so these endpoints
    /// accept a POST with the filter in the body and are otherwise unchanged.
    /// </summary>
    public class ContactFilterBody
    {
        public string? FiltersJson { get; set; }
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
        public string? CampaignId { get; set; }
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
