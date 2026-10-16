using Newtonsoft.Json;

namespace PitchGenApi.Model
{
    /// <summary>
    /// A note that one check's score was set to 100 by a person rather than
    /// earned by a run.
    /// </summary>
    public sealed class ManualScoreMark
    {
        [JsonProperty("at")]
        public DateTime At { get; set; }

        [JsonProperty("by")]
        public string? By { get; set; }
    }

    /// <summary>
    /// Reads and writes <see cref="ContactValidation.ManualScoresJson"/>: which
    /// of the four checks currently hold a hand-set 100, when, and who set it.
    ///
    /// The marks live in one JSON column keyed by check rather than in four
    /// bit columns because nothing sorts, filters or exports on them — they are
    /// read once, by the score cell, to explain a number that would otherwise
    /// look like a perfect run. One nullable column also means one DDL change
    /// if a fifth check is ever added.
    ///
    /// A mark is deliberately not evidence of anything beyond "a person said
    /// so". It is cleared the moment that check runs again, because the run is
    /// newer evidence and the 100 it overwrote is gone — leaving the mark
    /// behind would have the grid claim a fresh model score had been typed in
    /// by hand.
    /// </summary>
    public static class ManualScoreMarks
    {
        public static Dictionary<string, ManualScoreMark> Read(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new();

            try
            {
                return JsonConvert.DeserializeObject<Dictionary<string, ManualScoreMark>>(json)
                       ?? new Dictionary<string, ManualScoreMark>();
            }
            catch (JsonException)
            {
                // A malformed blob costs an explanatory line in a popover. It
                // must never cost the scores on the row it sits beside.
                return new Dictionary<string, ManualScoreMark>();
            }
        }

        /// <summary>Records the mark for one check, replacing any earlier one.</summary>
        public static string? Set(string? json, string checkType, DateTime at, string? by)
        {
            var marks = Read(json);

            marks[ValidationCheckTypes.Normalize(checkType)] = new ManualScoreMark
            {
                At = at,
                By = string.IsNullOrWhiteSpace(by) ? null : by.Trim()
            };

            return Write(marks);
        }

        /// <summary>
        /// Drops one check's mark, for when that check has just produced a
        /// score of its own.
        /// </summary>
        public static string? Clear(string? json, string checkType)
        {
            if (string.IsNullOrWhiteSpace(json)) return json;

            var marks = Read(json);

            return marks.Remove(ValidationCheckTypes.Normalize(checkType))
                ? Write(marks)
                : json;
        }

        /// <summary>
        /// An empty set is stored as null rather than "{}", so "has anything
        /// been set by hand?" stays a null check in SQL as well as in the
        /// browser.
        /// </summary>
        private static string? Write(Dictionary<string, ManualScoreMark> marks) =>
            marks.Count == 0 ? null : JsonConvert.SerializeObject(marks);
    }
}
