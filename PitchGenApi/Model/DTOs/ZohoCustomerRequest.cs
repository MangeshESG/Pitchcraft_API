using Newtonsoft.Json;

public class ZohoCustomerRequest
{
    [JsonProperty("display_name")]
    public string DisplayName { get; set; }

    [JsonProperty("first_name")]
    public string FirstName { get; set; }

    [JsonProperty("last_name")]
    public string LastName { get; set; }

    [JsonProperty("email")]
    public string Email { get; set; }

    [JsonProperty("mobile")]
    public string Mobile { get; set; }

    [JsonProperty("billing_address")]
    public Address BillingAddress { get; set; }

    [JsonProperty("currency_code")]
    public string CurrencyCode { get; set; }
}

public class Address
{

    [JsonProperty("street")]
    public string Street { get; set; }

    [JsonProperty("city")]
    public string City { get; set; }

    [JsonProperty("state")]
    public string State { get; set; }

    [JsonProperty("zip")]
    public string Zip { get; set; }

    [JsonProperty("country")]
    public string Country { get; set; }

    [JsonProperty("state_code")]
    public string StateCode { get; set; }
}
