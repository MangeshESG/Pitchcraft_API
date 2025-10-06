using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using PitchGenApi.Database;
using PitchGenApi.Model.DTOs;
using PitchGenApi.Services;
using System.Text;

namespace PitchGenApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class PlaneController : ControllerBase
    {
        private readonly ZohoSubscriptionService _zohoSubscriptionService;

        public PlaneController(ZohoSubscriptionService zohoSubscriptionService)
        {
            _zohoSubscriptionService = zohoSubscriptionService;
        }


        [HttpPost("create-customer")]
        public async Task<IActionResult> CreateCustomer([FromQuery] int ClinteId, [FromBody] ZohoCustomerRequest customer)
        {
            try
            {
                var result = await _zohoSubscriptionService.CreateCustomerAsync(customer, ClinteId);

                if (string.IsNullOrEmpty(result))
                {
                    return BadRequest(new { message = "Something went wrong" });
                }
                else
                {
                    return Ok(new { customer_id = result });
                }
            }
            catch (Exception ex)
            {
                // Optionally log the exception
                return BadRequest(new { error = ex.Message });
            }
        }

        [HttpPost("new-subscription")]
        public async Task<IActionResult> CreateNewSubscription([FromQuery] int clientId, [FromBody] ZohoSubscriptionRequest requestModel)
        {
            try
            {
                var result = await _zohoSubscriptionService.CreateNewSubscriptionAsync(requestModel, clientId);
                return Ok(result);
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }

    }
}
