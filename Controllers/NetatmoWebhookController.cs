using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using RIoT2.Net.Devices.Services.Interfaces;

namespace RIoT2.Net.Devices.Controllers
{
    /// <summary>
    /// Netatmo's webhook: <c>POST /api/netatmo/webhook/{secret}</c>. Netatmo can't sign in, so the route is anonymous and
    /// the secret in the URL is the credential. The push is queued and answered at once: Netatmo deactivates a webhook
    /// that answers slowly or fails.
    /// </summary>
    [ApiController]
    [AllowAnonymous]
    [Route("api/netatmo/webhook")]
    public class NetatmoWebhookController : ControllerBase
    {
        internal const int MaxBodyBytes = 65536;

        private readonly INetatmoWebhookHub _hub;
        private readonly ILogger _logger;

        public NetatmoWebhookController(INetatmoWebhookHub hub, ILogger logger)
        {
            _hub = hub;
            _logger = logger;
        }

        [HttpPost("{secret}")]
        [RequestSizeLimit(MaxBodyBytes)]
        public async Task<IResult> ReceiveAsync(string secret, CancellationToken cancellationToken)
        {
            string body;
            using (var reader = new StreamReader(Request.Body))
                body = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            if (body.Length > MaxBodyBytes)
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

            if (Request.Headers.ContainsKey("X-Netatmo-Secret"))
                _logger.LogDebug("A Netatmo webhook push carries an X-Netatmo-Secret header; it isn't verified.");

            // No detail for an unknown secret, and the secret is never logged.
            return _hub.TryAccept(secret, body) ? Results.Ok() : Results.NotFound();
        }
    }
}
