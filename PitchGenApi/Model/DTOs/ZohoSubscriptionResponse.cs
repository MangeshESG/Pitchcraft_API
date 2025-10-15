namespace PitchGenApi.Model.DTOs.Subscription
{
    public class ZohoSubscriptionResponse
{
    public int code { get; set; }
    public string message { get; set; }
    public Subscription subscription { get; set; }
}

public class Subscription
{
    public string subscription_id { get; set; }
    public Plan plan { get; set; }
    public Customer customer { get; set; }
}

public class Plan
{
    public string plan_id { get; set; }
    public string name { get; set; }
}

public class Customer
{
    public string customer_id { get; set; }
    public string display_name { get; set; }
    public string email { get; set; }
}

}
