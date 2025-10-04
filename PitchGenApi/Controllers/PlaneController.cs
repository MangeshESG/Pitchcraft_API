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
        public async Task<IActionResult> CreateCustomer([FromBody] ZohoCustomerRequest customer)
        {
            try
            {
                var result = await _zohoSubscriptionService.CreateCustomerAsync(customer);
                return Content(result, "application/json");
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }

        [HttpPost("new-subscription")]
        public async Task<IActionResult> CreateNewSubscription([FromBody] ZohoSubscriptionRequest requestModel)
        {
            try
            {
                var result = await _zohoSubscriptionService.CreateNewSubscriptionAsync(requestModel);
                return Ok(result);
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }

    }
}
