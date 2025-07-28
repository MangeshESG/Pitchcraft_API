using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace PitchGenApi.Models
{
    [Table("contacts")]
    public class Contact
    {
        public int id { get; set; }
        [Column("data_file_id")]
        public int DataFileId { get; set; }

        public string? full_name { get; set; }
        public string? email { get; set; }
        public string? website { get; set; }
        public string? company_name { get; set; }
        public string? job_title { get; set; }
        public string? linkedin_url { get; set; }
        public string? country_or_address { get; set; }
        public string? email_subject { get; set; }
        public string? email_body { get; set; }

        public DateTime created_at { get; set; }
        public DateTime? updated_at { get; set; }

        public DateTime? email_sent_at { get; set; } // Nullable to allow for unset values

        public DataFile data_file { get; set; } // Navigation
    }
}
