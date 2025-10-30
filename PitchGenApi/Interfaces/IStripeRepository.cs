using PitchGenApi.Model.DTOs;
using Stripe;
using System.Threading.Tasks;

namespace PitchGenApi.Repositories
{
    public interface IStripeRepository
    {
        //Task HandleCheckoutCompletedAsync(Event stripeEvent);
        Task HandleInvoicePaidAsync(Event stripeEvent);
        Task HandleSubscriptionCancelledAsync(Event stripeEvent);
        Task SaveUserCreditsAsync(int userId, string planId, string stripeSubscriptionId, string SubcribtionNumber);
        Task<StripeInvoiceResponse?> GetInvoiceDetailsAsync(string invoiceId);
        Task<List<Stripe.Subscription>> GetAllSubscriptionsByCustomerAsync(string customerId);
    }
}
