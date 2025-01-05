using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using RIoT2.Net.Devices.Services.Interfaces;

namespace RIoT2.Net.Devices.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class WebhookController : ControllerBase
    {
        private IWebhookService _webhookService;
        public WebhookController(IWebhookService webhookService)
        {
            _webhookService = webhookService;
        }

        [HttpPost]
        [Route("{address}")]
        public IResult SaveAsync(string address, [FromBody] object content)
        {
            _webhookService.SetWebhook(address, content.ToString());
            return Results.Ok();
        }

    }
}