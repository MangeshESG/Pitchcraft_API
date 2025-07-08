using Newtonsoft.Json;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace PitchGenApi.Model
{
        public class ZohoApiResponse
        {
            public List<Contactinfo> Data { get; set; }
            public Info Info { get; set; }
        }
        public class Contactinfo
        {
            public string Full_Name { get; set; }
            public string Email { get; set; }
            public string Website { get; set; }

            public string Account_name_friendlySingle_Line_12 { get; set; }

            public string Job_Title { get; set; }
            public string id { get; set; }
            public string Sample_email_body { get; set; }

            public string PG_Processed_on1 { get; set; }

            public string LinkedIn_URL { get; set; }
            public string Mailing_Country { get; set; }
            public AccountNameInfo Account_Name { get; set; }
            public string account_id { get; set; }
            public DateTime? Last_Email_Body_updated { get; set; }
            public bool? PG_added_correctly { get; set; }

            [JsonPropertyName("email_subject")]
            public string job_post_URL { get; set; }
        }

        public class Info
        {
            public bool Call { get; set; }
            public int Per_Page { get; set; }
            public string Next_Page_Token { get; set; }
            public int Count { get; set; }
            public bool more_records { get; set; }
            public string Previous_Page_Token { get; set; }

        }
        public class AccountNameInfo
        {
            public string name { get; set; }
            public string id { get; set; }

        }

        public class AccountInfo
        {
            public string PG_Job_title { get; set; }
            public string Email_body { get; set; }
            public string Email_Subject { get; set; }
            public string Last_used_PG_email_template1 { get; set; }
        }
    // Add the new UpdateZohoRequest class here
        public class UpdateZohoRequest
        {
            public string ContactId { get; set; }
            public string AccountId { get; set; }
            [Required]
            public string EmailBody { get; set; }

            [JsonPropertyName("email_subject")]
            public string job_post_URL { get; set; }
        }
}
