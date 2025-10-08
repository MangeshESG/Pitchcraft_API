using System.Text.Json.Serialization;

namespace PitchGenApi.Model.DTOs
{
    public class ZohoCustomerDTO
    {
        public int id { get; set; }
        public string customer_id { get; set; }
        public string contact_id { get; set; }
        public string primary_contactperson_id { get; set; }
        public string display_name { get; set; }
        public string contact_name { get; set; }
        public string first_name { get; set; }
        public string last_name { get; set; }
        public string email { get; set; }
        public string mobile { get; set; }
        public string status { get; set; }

    }

    public class ZohoCustomerResponse
    {
        public int code { get; set; }

        public string message { get; set; }

        public ZohoCustomerDTO customer { get; set; }
    }
}
