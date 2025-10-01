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

    [JsonProperty("company_name")]
    public string CompanyName { get; set; }

    [JsonProperty("phone")]
    public string Phone { get; set; }

    [JsonProperty("mobile")]
    public string Mobile { get; set; }

    [JsonProperty("billing_address")]
    public Address BillingAddress { get; set; }

    [JsonProperty("shipping_address")]
    public Address ShippingAddress { get; set; }

    [JsonProperty("currency_code")]
    public string CurrencyCode { get; set; }

    [JsonProperty("is_portal_enabled")]
    public bool IsPortalEnabled { get; set; }

    [JsonProperty("payment_terms")]
    public int PaymentTerms { get; set; }

    [JsonProperty("payment_terms_label")]
    public string PaymentTermsLabel { get; set; }
}

public class Address
{
    [JsonProperty("attention")]
    public string Attention { get; set; }

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

    [JsonProperty("fax")]
    public string Fax { get; set; }
}
