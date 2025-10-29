using PitchGenApi.Model.DTOs;
using Stripe;
using System.Threading.Tasks;

namespace PitchGenApi.Repositories
{
    public interface IStripeRepository
    {
        Task HandleCheckoutCompletedAsync(Event stripeEvent);
        Task HandleInvoicePaidAsync(Event stripeEvent);
        Task HandleSubscriptionCancelledAsync(Event stripeEvent);
        Task SaveUserCreditsAsync(int userId, string planId, string stripeSubscriptionId);
        Task<StripeInvoiceResponse?> GetInvoiceDetailsAsync(string invoiceId);
    }
}
