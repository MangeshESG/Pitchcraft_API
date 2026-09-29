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
        Task HandlePaymentIntentSucceededAsync(Event stripeEvent);
        Task SaveUserCreditsAsync(int userId, string planId, string? stripeSubscriptionId, string SubcribtionNumber, DateTime? StartDate, DateTime? EndDate,string? interval, decimal? amount, int? CreditsCount);
        Task<object?> GetActivePlanStatusAndPlaneAsync(int clientId);
        //Task<StripeSubscriptionResponse> GetAllSubscriptionsByCustomerAsync(string clientId, int limit = 10, string? startingAfter = null);
        Task<PlanHistoryPagedResult<object>> GetPlanHistoryByClientIdAsync(int clientId, int pageNumber = 1, int pageSize = 10);
        Task<string> CreateCreditPurchaseIntentAsync(string userId, int credits);

        /// <summary>
        /// Takes credits off a client's balance, custom credit first, then the
        /// plan allowance. All-or-nothing: a request for more than they hold
        /// removes nothing and comes back with the balance.
        /// </summary>
        Task<ReduceUserCreditsResult> ReduceUserCreditsAsync(int clientId, int credits, string? reason);

    }
}