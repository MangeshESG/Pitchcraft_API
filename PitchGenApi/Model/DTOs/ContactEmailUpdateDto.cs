namespace PitchGenApi.Model.DTOs
{
    public class ContactEmailUpdateDto
    {
        public int ClientId { get; set; }
        public int ContactId { get; set; }
        public int? campaignId { get; set; }
        public int? blueprintId { get; set; }
        public bool? GPTGenerate { get; set; }
        public string? EmailSubject { get; set; }
        public string? EmailBody { get; set; }

        /// <summary>
        /// The source highlights that belong to the body being saved, as the
        /// JSON array the generation endpoint returned for it.
        ///
        /// Sent when the body comes from a generated option the user picked,
        /// so the stored highlights describe the stored email rather than the
        /// one that happened to be generated first. Null leaves whatever is
        /// stored untouched, which is what every other caller wants: an
        /// ordinary draft save edits the body and must not silently claim the
        /// highlights no longer match.
        /// </summary>
        public string? EmailHighlights { get; set; }
    }
}
