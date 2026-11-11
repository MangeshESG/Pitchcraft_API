namespace PitchGenApi.Model.DTOs
{
    public class GenerateSingleContactCampaignEmailRequest
    {
        public int BlueprintId { get; set; }
        public int ContactId { get; set; }
        public string ClientId { get; set; } = "";
        public bool OverwriteExisting { get; set; } = true;
        public bool Preview { get; set; } = false;

        /// <summary>
        /// A one-off instruction typed next to the email in the editor, applied
        /// to this generation only. It fills the blueprint's
        /// {manual_instruction} slot; nothing is stored, so the next generation
        /// for the same contact starts clean again.
        /// </summary>
        public string? ManualInstruction { get; set; }

        /// <summary>
        /// Which Settings &gt; AI models purpose should pick the writing model
        /// for this call: <see cref="AiModelPurposes.TransactionalEmail"/> for
        /// an inbox reply/forward or a profile Compose draft, and
        /// <see cref="AiModelPurposes.EmailGeneration"/> (the default) for a
        /// campaign kraft.
        ///
        /// One endpoint serves both surfaces, so the caller is the only thing
        /// that knows which it is. Anything unrecognised — including an older
        /// client that sends nothing — resolves to the kraft purpose, which is
        /// the model every one of these calls used before the split.
        /// </summary>
        public string? ModelPurpose { get; set; }

        /// <summary>
        /// The purpose key to read the writing model from, narrowed to the two
        /// keys this endpoint serves so the field can't be used to point a
        /// generation at an unrelated purpose's model.
        /// </summary>
        public string ResolvedModelPurpose =>
            string.Equals(
                (ModelPurpose ?? "").Trim(),
                AiModelPurposes.TransactionalEmail,
                StringComparison.OrdinalIgnoreCase)
                ? AiModelPurposes.TransactionalEmail
                : AiModelPurposes.EmailGeneration;
    }
}
