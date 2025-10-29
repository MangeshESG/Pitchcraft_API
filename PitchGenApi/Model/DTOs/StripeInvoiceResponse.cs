namespace PitchGenApi.Model.DTOs
{
    public class StripeInvoiceResponse
    {
        public string InvoiceId { get; set; }
        public string CustomerEmail { get; set; }
        public string CustomerName { get; set; }
        public string InvoiceNumber { get; set; }
        public DateTime InvoiceDate { get; set; }
        public decimal AmountPaid { get; set; }
        public string InvoicePdfUrl { get; set; }
    }

}
