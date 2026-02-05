namespace PitchGenApi.Model.DTOs
{
    public class UpdateNotesDto
    {
        public int NoteId { get; set; }
        public int clientId { get; set; }
        public int contactId { get; set; }
        public string Note { get; set; }
        public bool IsPin { get; set; }
        public bool IsUseInGenration { get; set; }
    }
}
