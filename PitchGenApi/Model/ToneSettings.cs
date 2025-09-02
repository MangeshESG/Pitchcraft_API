using System;
using System.ComponentModel.DataAnnotations;

namespace PitchGenApi.Models
{
    public class ToneSettings
    {
        [Key]
        public int Id { get; set; }

        public int ClientId { get; set; }

        public string Language { get; set; }

        public string SubjectTemplate { get; set; }

        public string Emojis { get; set; }

        public string Tone { get; set; }

        public string ChattyLevel { get; set; }

        public string CreativityLevel { get; set; }

        public string ReasoningLevel { get; set; }

        public string DateGreeting { get; set; }

        public string DateFarewell { get; set; }

        public DateTime CreatedAt { get; set; }

        public DateTime? UpdatedAt { get; set; }
    }
}