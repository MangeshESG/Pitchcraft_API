using PitchGenApi.Model;
using PitchGenApi.Model.DTOs;
using Stripe;
using System.Threading.Tasks;
using UglyToad.PdfPig.Graphics.Operations.PathPainting;

namespace PitchGenApi.Repositories
{
    public interface IStripeRepository
    {
        Task<CreateSubscriptionResponse> CreateSubscriptionAsync(CreateSubscriptionRequest req);
        Task HandleInvoicePaidAsync(Event stripeEvent);
        Task HandleSubscriptionCancelledAsync(Event stripeEvent);
        Task HandleWebhookEventAsync(Event stripeEvent);
        Task SaveUserCreditsAsync(int userId, string planId, string stripeSubscriptionId, string SubcribtionNumber, DateTime StartDate, DateTime EndDate);
        Task<StripeInvoiceResponse?> GetInvoiceDetailsAsync(string invoiceId);
        //Task<StripeSubscriptionResponse> GetAllSubscriptionsByCustomerAsync(string clientId, int limit = 10, string? startingAfter = null);
        Task<PlanHistoryPagedResult<object>> GetPlanHistoryByClientIdAsync(int clientId, int pageNumber = 1, int pageSize = 10);
        Task<string> CreateCreditPurchaseIntentAsync(string userId, int credits);

    }
}