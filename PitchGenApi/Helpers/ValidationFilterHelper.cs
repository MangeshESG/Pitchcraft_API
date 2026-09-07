using PitchGenApi.Model.DTOs;

namespace PitchGenApi.Helpers
{
    /// <summary>
    /// The Audience Assurance fields a saved view can be filtered on.
    ///
    /// The scores live in their own table and are normally loaded for the page
    /// being returned — one row per contact on screen. A filter changes that:
    /// the scores decide which contacts are on the page in the first place, so
    /// they have to be in hand before the filter runs, for every contact the
    /// view could return. That is a materially bigger query, so it is only
    /// worth paying for when a saved filter actually mentions one of these
    /// fields — which is what this helper is for.
    ///
    /// The keys match the ones the grid renders and the filter picker offers
    /// (src/components/feature/validation/validationColumns.tsx).
    /// </summary>
    public static class ValidationFilterHelper
    {
        public static readonly HashSet<string> FieldKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "contactFitConfidence",
            "dataIntegrityConfidence",
            "liveContactConfidence",
            "emailValidityConfidence",
            "isVerified",
            "lastChecked"
        };

        public static bool IsValidationField(string? field) =>
            !string.IsNullOrWhiteSpace(field) && FieldKeys.Contains(field.Trim());

        /// <summary>
        /// True when the saved filter asks about at least one validation score,
        /// so the scores have to be loaded for the whole view rather than for
        /// the returned page.
        /// </summary>
        public static bool UsesValidationFields(FiltersPayload? payload) =>
            TrackingFilterHelper.NormalizeGroups(payload)
                .SelectMany(group => group.Conditions ?? new List<FilterConditionDto>())
                .Any(cond => TrackingFilterHelper.IsCompleteCondition(cond) &&
                             IsValidationField(cond.Field));
    }
}
