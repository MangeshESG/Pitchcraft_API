namespace PitchGenApi.Model.DTOs
{
    using Newtonsoft.Json;

    public class ZohoSubscriptionRequest
    {
        public string customer_id { get; set; }
        public Customer customer { get; set; }
        public Plan plan { get; set; }
        public List<PaymentGateway> payment_gateways { get; set; }
    }

    public class Customer
    {
        public string display_name { get; set; }
        public string email { get; set; }
    }

    public class Plan
    {
        public string plan_code { get; set; }
        public int quantity { get; set; }
    }

    public class PaymentGateway
    {
        public string payment_gateway { get; set; }
    }

}
