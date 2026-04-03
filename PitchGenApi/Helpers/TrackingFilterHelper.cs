using Microsoft.EntityFrameworkCore;
using PitchGenApi.Database;
using PitchGenApi.Model.DTOs;
using System.Reflection;
using System.Text.Json;

namespace PitchGenApi.Helpers
{
    public static class TrackingFilterHelper
    {
        public const string AllCampaignsId = "__all__";

        public sealed class TrackingFilterContext
        {
            public Dictionary<int, CampaignTrackingBucket> Campaigns { get; } = new();
        }

        public sealed class CampaignTrackingBucket
        {
            public HashSet<int> SentContactIds { get; } = new();
            public HashSet<string> SentEmails { get; } = new(StringComparer.OrdinalIgnoreCase);

            public HashSet<int> OpenedContactIds { get; } = new();
            public HashSet<string> OpenedEmails { get; } = new(StringComparer.OrdinalIgnoreCase);

            public HashSet<int> ClickedContactIds { get; } = new();
            public HashSet<string> ClickedEmails { get; } = new(StringComparer.OrdinalIgnoreCase);
        }

        public static bool IsTrackingField(string? field)
        {
            return string.Equals(field, "tracking_open", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(field, "tracking_click", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsAllCampaigns(FilterConditionDto cond) =>
            string.Equals(cond.Context?.CampaignId, AllCampaignsId, StringComparison.OrdinalIgnoreCase);

        public static int? ParseCampaignId(FilterConditionDto cond)
        {
            var raw = cond.Context?.CampaignId;

            if (string.IsNullOrWhiteSpace(raw) || IsAllCampaigns(cond))
                return null;

            return int.TryParse(raw, out var campaignId) ? campaignId : null;
        }

        public static List<FilterGroupDto> NormalizeGroups(FiltersPayload? payload)
        {
            if (payload?.Groups != null && payload.Groups.Count > 0)
                return payload.Groups;

            if (payload?.Conditions != null && payload.Conditions.Count > 0)
            {
                return new List<FilterGroupDto>
                {
                    new FilterGroupDto
                    {
                        JoinWithPrevious = "AND",
                        Conditions = payload.Conditions
                    }
                };
            }

            return new List<FilterGroupDto>();
        }

        public static string? ValidateTrackingFilters(FiltersPayload? payload)
        {
            var groups = NormalizeGroups(payload);

            foreach (var cond in groups.SelectMany(g => g.Conditions ?? new List<FilterConditionDto>()))
            {
                if (!IsCompleteCondition(cond))
                    continue;

                if (IsTrackingField(cond.Field) && string.IsNullOrWhiteSpace(cond.Context?.CampaignId))
                    return $"CampaignId is required for tracking filter field '{cond.Field}'.";
            }

            return null;
        }

        public static bool IsCompleteCondition(FilterConditionDto cond)
        {
            if (string.IsNullOrWhiteSpace(cond.Field))
                return false;

            if (string.IsNullOrWhiteSpace(cond.Operator))
                return false;

            if (string.IsNullOrWhiteSpace(cond.Value))
                return false;

            if (IsTrackingField(cond.Field) && string.IsNullOrWhiteSpace(cond.Context?.CampaignId))
                return false;

            return true;
        }

        public static async Task<TrackingFilterContext> BuildTrackingFilterContextAsync(
            AppDbContext context,
            int clientId,
            FiltersPayload? payload,
            List<int> contactIds,
            List<string> contactEmails
        )
        {
            var trackingContext = new TrackingFilterContext();
            var groups = NormalizeGroups(payload);

            var trackingConditions = groups
                .SelectMany(g => g.Conditions ?? new List<FilterConditionDto>())
                .Where(c => IsTrackingField(c.Field))
                .ToList();

            var includesAllCampaigns = trackingConditions.Any(IsAllCampaigns);

            List<int> campaignIds;

            if (includesAllCampaigns)
            {
                campaignIds = await context.Campaigns
                    .Where(c => c.ClientId == clientId)
                    .Select(c => c.Id)
                    .Distinct()
                    .ToListAsync();
            }
            else
            {
                campaignIds = trackingConditions
                    .Select(ParseCampaignId)
                    .Where(id => id.HasValue)
                    .Select(id => id!.Value)
                    .Distinct()
                    .ToList();
            }

            if (!campaignIds.Any())
                return trackingContext;

            var emailLogs = await context.EmailLogs
                .Where(x =>
                    x.ClientId == clientId &&
                    x.CampaignId.HasValue &&
                    campaignIds.Contains(x.CampaignId.Value) &&
                    (
                        (x.ContactId.HasValue && contactIds.Contains(x.ContactId.Value)) ||
                        contactEmails.Contains((x.ToEmail ?? "").ToLower())
                    )
                )
                .Select(x => new
                {
                    CampaignId = x.CampaignId!.Value,
                    ContactId = x.ContactId,
                    ToEmail = x.ToEmail
                })
                .ToListAsync();

            foreach (var item in emailLogs)
            {
                var bucket = GetCampaignBucket(trackingContext, item.CampaignId);

                if (item.ContactId.HasValue)
                    bucket.SentContactIds.Add(item.ContactId.Value);

                var email = NormalizeEmail(item.ToEmail);
                if (!string.IsNullOrWhiteSpace(email))
                    bucket.SentEmails.Add(email);
            }

            var trackingLogs = await context.EmailTrackingLogs
                .Where(x =>
                    x.ClientId == clientId &&
                    x.CampaignId.HasValue &&
                    campaignIds.Contains(x.CampaignId.Value) &&
                    (
                        (x.ContactId.HasValue && contactIds.Contains(x.ContactId.Value)) ||
                        contactEmails.Contains((x.Email ?? "").ToLower())
                    )
                )
                .Select(x => new
                {
                    CampaignId = x.CampaignId!.Value,
                    ContactId = x.ContactId,
                    Email = x.Email,
                    EventType = x.EventType
                })
                .ToListAsync();

            foreach (var item in trackingLogs)
            {
                var bucket = GetCampaignBucket(trackingContext, item.CampaignId);
                var eventType = (item.EventType ?? "").Trim();

                if (eventType.Equals("Open", StringComparison.OrdinalIgnoreCase))
                {
                    if (item.ContactId.HasValue)
                        bucket.OpenedContactIds.Add(item.ContactId.Value);

                    var email = NormalizeEmail(item.Email);
                    if (!string.IsNullOrWhiteSpace(email))
                        bucket.OpenedEmails.Add(email);
                }
                else if (eventType.Equals("Click", StringComparison.OrdinalIgnoreCase))
                {
                    if (item.ContactId.HasValue)
                        bucket.ClickedContactIds.Add(item.ContactId.Value);

                    var email = NormalizeEmail(item.Email);
                    if (!string.IsNullOrWhiteSpace(email))
                        bucket.ClickedEmails.Add(email);
                }
            }

            return trackingContext;
        }

        public static List<T> ApplyFilters<T>(
            List<T> data,
            FiltersPayload? payload,
            TrackingFilterContext trackingContext
        )
        {
            var groups = NormalizeGroups(payload);
            if (groups.Count == 0)
                return data;

            return data.Where(row =>
            {
                bool overallResult = true;

                for (int g = 0; g < groups.Count; g++)
                {
                    var group = groups[g];
                    var conditions = (group.Conditions ?? new List<FilterConditionDto>())
                        .Where(IsCompleteCondition)
                        .ToList();

                    if (conditions.Count == 0)
                        continue;

                    bool groupResult = true;

                    for (int i = 0; i < conditions.Count; i++)
                    {
                        var cond = conditions[i];
                        bool eval = EvaluateCondition(row!, cond, trackingContext);

                        if (i == 0)
                        {
                            groupResult = eval;
                        }
                        else
                        {
                            var join = (cond.JoinWithPrevious ?? "AND").ToUpperInvariant();
                            groupResult = join == "OR" ? (groupResult || eval) : (groupResult && eval);
                        }
                    }

                    if (g == 0)
                    {
                        overallResult = groupResult;
                    }
                    else
                    {
                        var join = (group.JoinWithPrevious ?? "AND").ToUpperInvariant();
                        overallResult = join == "OR" ? (overallResult || groupResult) : (overallResult && groupResult);
                    }
                }

                return overallResult;
            }).ToList();
        }

        private static bool EvaluateCondition(
            object row,
            FilterConditionDto cond,
            TrackingFilterContext trackingContext
        )
        {
            if (IsTrackingField(cond.Field))
                return EvaluateTrackingCondition(row, cond, trackingContext);

            var rawValue = GetFieldValue(row, cond.Field ?? "");
            var target = cond.Value ?? "";

            switch ((cond.Operator ?? "").Trim())
            {
                case "contains":
                    return rawValue.Contains(target, StringComparison.OrdinalIgnoreCase);

                case "equals":
                    return rawValue.Equals(target, StringComparison.OrdinalIgnoreCase);

                case "notEquals":
                    return !rawValue.Equals(target, StringComparison.OrdinalIgnoreCase);

                case "startsWith":
                    return rawValue.StartsWith(target, StringComparison.OrdinalIgnoreCase);

                case "endsWith":
                    return rawValue.EndsWith(target, StringComparison.OrdinalIgnoreCase);

                case "gt":
                    {
                        var a = ToNumberSafe(rawValue);
                        var b = ToNumberSafe(target);
                        return a.HasValue && b.HasValue && a > b;
                    }

                case "lt":
                    {
                        var a = ToNumberSafe(rawValue);
                        var b = ToNumberSafe(target);
                        return a.HasValue && b.HasValue && a < b;
                    }

                case "gte":
                    {
                        var a = ToNumberSafe(rawValue);
                        var b = ToNumberSafe(target);
                        return a.HasValue && b.HasValue && a >= b;
                    }

                case "lte":
                    {
                        var a = ToNumberSafe(rawValue);
                        var b = ToNumberSafe(target);
                        return a.HasValue && b.HasValue && a <= b;
                    }

                case "before":
                    {
                        var a = ToDateSafe(rawValue);
                        var b = ToDateSafe(target);
                        return a.HasValue && b.HasValue && a < b;
                    }

                case "after":
                    {
                        var a = ToDateSafe(rawValue);
                        var b = ToDateSafe(target);
                        return a.HasValue && b.HasValue && a > b;
                    }

                default:
                    return true;
            }
        }

        private static bool EvaluateTrackingCondition(
            object row,
            FilterConditionDto cond,
            TrackingFilterContext trackingContext
        )
        {
            var operatorValue = (cond.Operator ?? "").Trim();
            if (!operatorValue.Equals("equals", StringComparison.OrdinalIgnoreCase) &&
                !operatorValue.Equals("notEquals", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var wantsTrue = string.Equals(cond.Value, "true", StringComparison.OrdinalIgnoreCase);

            var contactId = ToIntSafe(GetFieldValue(row, "id"));
            var email = NormalizeEmail(GetFieldValue(row, "email"));

            List<CampaignTrackingBucket> buckets;

            if (IsAllCampaigns(cond))
            {
                buckets = trackingContext.Campaigns.Values.ToList();
            }
            else
            {
                var campaignId = ParseCampaignId(cond);
                if (!campaignId.HasValue ||
                    !trackingContext.Campaigns.TryGetValue(campaignId.Value, out var bucket))
                {
                    return false;
                }

                buckets = new List<CampaignTrackingBucket> { bucket };
            }

            if (!buckets.Any())
                return false;

            var sentInCampaign = buckets.Any(bucket =>
                (contactId.HasValue && bucket.SentContactIds.Contains(contactId.Value)) ||
                (!string.IsNullOrWhiteSpace(email) && bucket.SentEmails.Contains(email))
            );

            bool eventOccurred;

            if (string.Equals(cond.Field, "tracking_open", StringComparison.OrdinalIgnoreCase))
            {
                eventOccurred = buckets.Any(bucket =>
                    (contactId.HasValue && bucket.OpenedContactIds.Contains(contactId.Value)) ||
                    (!string.IsNullOrWhiteSpace(email) && bucket.OpenedEmails.Contains(email))
                );
            }
            else if (string.Equals(cond.Field, "tracking_click", StringComparison.OrdinalIgnoreCase))
            {
                eventOccurred = buckets.Any(bucket =>
                    (contactId.HasValue && bucket.ClickedContactIds.Contains(contactId.Value)) ||
                    (!string.IsNullOrWhiteSpace(email) && bucket.ClickedEmails.Contains(email))
                );
            }
            else
            {
                return false;
            }

            var result = wantsTrue
                ? eventOccurred
                : sentInCampaign && !eventOccurred;

            if (operatorValue.Equals("notEquals", StringComparison.OrdinalIgnoreCase))
                result = !result;

            return result;
        }

        private static CampaignTrackingBucket GetCampaignBucket(
            TrackingFilterContext context,
            int campaignId
        )
        {
            if (!context.Campaigns.TryGetValue(campaignId, out var bucket))
            {
                bucket = new CampaignTrackingBucket();
                context.Campaigns[campaignId] = bucket;
            }

            return bucket;
        }

        private static string GetFieldValue(object row, string field)
        {
            var prop = row.GetType().GetProperty(
                field,
                BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance
            );

            if (prop != null)
            {
                var val = prop.GetValue(row);
                return val?.ToString() ?? "";
            }

            if (field.StartsWith("custom_", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var customProp =
                        row.GetType().GetProperty("custom_fields", BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance) ??
                        row.GetType().GetProperty("customFields", BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance) ??
                        row.GetType().GetProperty("custom_fields_json", BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance) ??
                        row.GetType().GetProperty("customFieldsJson", BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance) ??
                        row.GetType().GetProperty("CustomFields", BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);

                    var rawObj = customProp?.GetValue(row);

                    if (rawObj is IDictionary<string, object> dictObj)
                    {
                        var key = field.Replace("custom_", "", StringComparison.OrdinalIgnoreCase);
                        if (dictObj.TryGetValue(key, out var v))
                            return v?.ToString() ?? "";

                        var target = NormalizeKey(key);
                        foreach (var kv in dictObj)
                        {
                            if (NormalizeKey(kv.Key) == target)
                                return kv.Value?.ToString() ?? "";
                        }
                    }

                    if (rawObj is IDictionary<string, object?> dictNullable)
                    {
                        var key = field.Replace("custom_", "", StringComparison.OrdinalIgnoreCase);
                        if (dictNullable.TryGetValue(key, out var v))
                            return v?.ToString() ?? "";

                        var target = NormalizeKey(key);
                        foreach (var kv in dictNullable)
                        {
                            if (NormalizeKey(kv.Key) == target)
                                return kv.Value?.ToString() ?? "";
                        }
                    }

                    if (rawObj is IDictionary<string, string> dictStr)
                    {
                        var key = field.Replace("custom_", "", StringComparison.OrdinalIgnoreCase);
                        if (dictStr.TryGetValue(key, out var v))
                            return v ?? "";

                        var target = NormalizeKey(key);
                        foreach (var kv in dictStr)
                        {
                            if (NormalizeKey(kv.Key) == target)
                                return kv.Value ?? "";
                        }
                    }

                    var raw = rawObj?.ToString();
                    if (!string.IsNullOrWhiteSpace(raw))
                    {
                        var dict = JsonSerializer.Deserialize<Dictionary<string, object?>>(raw);
                        var key = field.Replace("custom_", "", StringComparison.OrdinalIgnoreCase);

                        if (dict != null)
                        {
                            if (dict.TryGetValue(key, out var v))
                                return v?.ToString() ?? "";

                            var target = NormalizeKey(key);
                            foreach (var kv in dict)
                            {
                                if (NormalizeKey(kv.Key) == target)
                                    return kv.Value?.ToString() ?? "";
                            }
                        }
                    }
                }
                catch
                {
                }
            }

            return "";
        }

        private static string NormalizeKey(string value) =>
            new string((value ?? "").ToLower().Where(char.IsLetterOrDigit).ToArray());

        private static string NormalizeEmail(string? value) =>
            string.IsNullOrWhiteSpace(value) ? "" : value.Trim().ToLowerInvariant();

        private static int? ToIntSafe(string? s)
        {
            if (int.TryParse(s, out var n))
                return n;

            return null;
        }

        private static double? ToNumberSafe(string s)
        {
            if (double.TryParse(s, out var n))
                return n;

            return null;
        }

        private static DateTime? ToDateSafe(string s)
        {
            if (DateTime.TryParse(s, out var d))
                return d;

            return null;
        }
    }
}
