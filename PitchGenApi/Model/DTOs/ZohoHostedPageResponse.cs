namespace PitchGenApi.Models.DTOs
{
    public class ZohoHostedPageResponse
    {
        public int code { get; set; }
        public string message { get; set; }
        public HostedPage hostedpage { get; set; }

        public class HostedPage
        {
            public string hostedpage_id { get; set; }
            public string decrypted_hosted_page_id { get; set; }
            public string status { get; set; }
            public string url { get; set; }
            public string action { get; set; }
            public string expiring_time { get; set; }
            public string created_time { get; set; }
        }
    }
}
