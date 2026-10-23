using System.Globalization;
using PitchGenApi.Model.DTOs;

namespace PitchGenApi.Helpers
{
    /// <summary>
    /// Today / Yesterday / Tomorrow as filter values.
    ///
    /// A date filter saved into a view has to keep meaning what it said: a view
    /// built as "Today" is expected to show today's rows every time it is
    /// opened, not the rows of the day it was built on. So the condition stores
    /// the token and the day is worked out here, each time the view is run.
    ///
    /// Which day that is depends on where the account is, so it comes from the
    /// time zone in Settings (UserDateTimeSettings) rather than from the
    /// server's own clock - the browser resolves the same token against the
    /// same setting, so the filter panel and the saved view agree.
    ///
    /// The keys match the ones the panel sends
    /// (src/components/common/relativeDateFilters.ts).
    /// </summary>
    public static class RelativeDateFilterHelper
    {
        private const string Today = "@today";
        private const string Yesterday = "@yesterday";
        private const string Tomorrow = "@tomorrow";

        public static bool IsRelativeDay(string? value)
        {
            var raw = (value ?? "").Trim();
            return raw.Equals(Today, StringComparison.OrdinalIgnoreCase) ||
                   raw.Equals(Yesterday, StringComparison.OrdinalIgnoreCase) ||
                   raw.Equals(Tomorrow, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// True when any condition names a relative day. Such a condition cannot
        /// be pushed into SQL - the stored column holds dates, not the word
        /// "today" - so the filter takes the in-memory path, which resolves it.
        /// </summary>
        public static bool UsesRelativeDays(FiltersPayload? payload) =>
            TrackingFilterHelper.NormalizeGroups(payload)
                .SelectMany(group => group.Conditions ?? new List<FilterConditionDto>())
                .Any(condition => IsRelativeDay(condition.Value));

        /// <summary>
        /// The calendar day a condition's value stands for: a token resolved in
        /// the account's time zone, or a day the user picked. Null for anything
        /// else, which is how a caller tells a date condition from the rest.
        /// </summary>
        public static DateOnly? ResolveDay(string? value, TimeZoneInfo timeZone)
        {
            var raw = (value ?? "").Trim();
            if (raw.Length == 0)
                return null;

            if (IsRelativeDay(raw))
            {
                var today = DateOnly.FromDateTime(
                    TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, timeZone));

                if (raw.Equals(Yesterday, StringComparison.OrdinalIgnoreCase))
                    return today.AddDays(-1);
                if (raw.Equals(Tomorrow, StringComparison.OrdinalIgnoreCase))
                    return today.AddDays(1);
                return today;
            }

            return DateOnly.TryParseExact(
                raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                ? day
                : null;
        }

        /// <summary>
        /// The calendar day a stored value falls on.
        ///
        /// A value carrying a time is an instant, and the ones in this database
        /// are written as UTC, so it is read in the account's time zone - 23:00
        /// UTC is already tomorrow in Delhi. A value that is only a date (what a
        /// custom date field holds) is that day already, and converting it would
        /// push it across midnight.
        /// </summary>
        public static DateOnly? DayOfStoredValue(string? rawValue, TimeZoneInfo timeZone)
        {
            var raw = (rawValue ?? "").Trim();
            if (raw.Length == 0)
                return null;

            if (DateOnly.TryParseExact(
                    raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var plainDay))
                return plainDay;

            if (!DateTime.TryParse(raw, out var parsed))
                return null;

            // No time in the text means there is no instant to place, so the date
            // is taken as written.
            if (!raw.Contains(':'))
                return DateOnly.FromDateTime(parsed);

            return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(parsed, DateTimeKind.Utc), timeZone));
        }

        /// <summary>
        /// A date condition compared by calendar day, for a stored value the
        /// filter engine has already turned into text.
        ///
        /// Null when the condition's value is not a day, or its operator is not
        /// one of these - the caller then keeps its own handling.
        /// </summary>
        public static bool? Evaluate(
            string? op,
            string? rawValue,
            string? conditionValue,
            TimeZoneInfo timeZone)
        {
            var targetDay = ResolveDay(conditionValue, timeZone);
            if (!targetDay.HasValue)
                return null;

            return Compare(op, DayOfStoredValue(rawValue, timeZone), targetDay.Value);
        }

        /// <summary>The same comparison for a stored UTC instant.</summary>
        public static bool? EvaluateInstant(
            string? op,
            DateTime instantUtc,
            string? conditionValue,
            TimeZoneInfo timeZone)
        {
            var targetDay = ResolveDay(conditionValue, timeZone);
            if (!targetDay.HasValue)
                return null;

            var rowDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(instantUtc, DateTimeKind.Utc), timeZone));

            return Compare(op, rowDay, targetDay.Value);
        }

        private static bool? Compare(string? op, DateOnly? rowDay, DateOnly targetDay)
        {
            switch ((op ?? "").Trim())
            {
                // The stored value is a timestamp, so comparing it against a day
                // as an instant answers "equals today" with nothing at all
                // (10:42 is not midnight) and "after today" with everything
                // since midnight. The days themselves are what the condition
                // reads as.
                case "equals":
                    return rowDay.HasValue && rowDay.Value == targetDay;
                case "notEquals":
                    return rowDay.HasValue && rowDay.Value != targetDay;
                case "before":
                    return rowDay.HasValue && rowDay.Value < targetDay;
                case "after":
                    return rowDay.HasValue && rowDay.Value > targetDay;
                default:
                    return null;
            }
        }
    }
}
