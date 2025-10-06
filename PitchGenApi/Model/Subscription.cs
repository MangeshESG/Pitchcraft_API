namespace PitchGenApi.Model
{
    public class Subscription
    {
        public int Id { get; set; }                  // Auto-incremented ID (Primary Key)
        public int ClientId { get; set; }            // Client ID (Foreign Key Reference, if applicable)
        public string CustomerId { get; set; }       // Customer ID (String to accommodate long numbers)
        public string PlanCode { get; set; }         // Plan Code (e.g., subscription plan identifier)
        public string CustomerName { get; set; }     // Customer's Name
        public string Email { get; set; }            // Customer's Email Address
        public int Quantity { get; set; }            // Subscription Quantity
        public string Payment_Gateway { get; set; }   // Payment Gateway (e.g., PayPal, Stripe)
        public DateTime? Createdat { get; set; }
    }
}
