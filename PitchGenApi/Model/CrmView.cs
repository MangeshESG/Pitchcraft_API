using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PitchGenApi.Model
{
    [Table("crm_views")]
    public class CrmView
    {
        [Key]
        public int id { get; set; }

        public int client_id { get; set; }

        public string name { get; set; }

        public string description { get; set; }

        public string filters_json { get; set; }

        public DateTime created_at { get; set; }
        public bool use_all_datafiles { get; set; } // NEW
    }
}