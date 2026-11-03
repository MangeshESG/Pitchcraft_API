using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace PitchGenApi.Models
{
    [Table("website_api_keys")]
    public class WebsiteContactIntegration
    {
        public int id { get; set; }
        public int client_id { get; set; }
        public int data_file_id { get; set; }
        public string website_name { get; set; } = "";
        public string key_hash { get; set; } = "";
        public DateTime created_at { get; set; }
    }
}
