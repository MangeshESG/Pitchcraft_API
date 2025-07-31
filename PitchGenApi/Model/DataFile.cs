using System;
using System.Collections.Generic;

namespace PitchGenApi.Models
{
    public class DataFile
    {
        public int id { get; set; }
        public int client_id { get; set; }
        public string? name { get; set; }
        public string? data_file_name { get; set; }
        public string? description { get; set; }
        public DateTime created_at { get; set; }
        public DateTime? updated_at { get; set; }

        public List<Contact> contacts { get; set; } = new();
    }
}
