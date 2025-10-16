using Microsoft.EntityFrameworkCore;
using PitchGenApi.Database;

namespace PitchGenApi
{
    public class ZohoDataService
    {
        private readonly AppDbContext _Context;

        public ZohoDataService(AppDbContext appDbContext)
        {
            _Context = appDbContext;
        }

        public async Task<object> GetCustomers(int clientId)
        {
            var customer = await _Context.ZohoCustomer
                .Where(c => c.ClientId == clientId)
                .Select(c => new {
                    c.CustomerId,
                    c.ClientId,
                    c.ContactName,
                    c.Email
                })
                .FirstOrDefaultAsync();

            return customer;
        } 
        
        public async Task<object> GetCustomersInClient(int clientId)
        {
            var customer = await _Context.ClientDetails
                .Where(c => c.Id == clientId)
                .Select(c => new {
                    c.FirstName,
                    c.LastName,
                    c.Email
                })
                .FirstOrDefaultAsync();

            return customer;
        }
    }
}
