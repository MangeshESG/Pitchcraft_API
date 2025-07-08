using System.Collections.Generic;

namespace PitchGenApi.DTOs
{
    public class DataFileWithContactsDto
    {
        public int clientId { get; set; }
        public string? name { get; set; }
        public string? dataFileName { get; set; }
        public string? description { get; set; }

        public List<ContactDto> contacts { get; set; }
    }
}
