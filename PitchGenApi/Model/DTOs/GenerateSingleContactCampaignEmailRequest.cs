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
    }
}
