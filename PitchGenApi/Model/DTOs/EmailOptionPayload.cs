using PitchGenApi.Services;

namespace PitchGenApi.Model.DTOs
{
    /// <summary>
    /// One generated email option on its way to the UI: a complete, sendable
    /// email with its own subject, body and highlights.
    ///
    /// A blueprint asks for one to five of these in a single generation
    /// (Email Option Count). The first is saved to the contact row straight
    /// away; the rest are offered in the editor until the user picks one, and
    /// are not stored anywhere — krafting again produces a fresh set.
    /// </summary>
    public sealed class EmailOptionPayload
    {
        /// <summary>
        /// A few plain words naming this option's angle, for the picker.
        /// Never part of the email itself.
        /// </summary>
        public string Label { get; set; } = "";

        public string Subject { get; set; } = "";
        public string BodyHtml { get; set; } = "";
        public List<EmailHighlight> Highlights { get; set; } = new();
    }
}
