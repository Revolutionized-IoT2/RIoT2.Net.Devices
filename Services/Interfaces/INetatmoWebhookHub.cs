using RIoT2.Net.Devices.Models;
using RIoT2.Net.Devices.Services.Netatmo;

namespace RIoT2.Net.Devices.Services.Interfaces
{
    /// <summary>
    /// Receives Netatmo's webhook pushes and registers the webhook, one per Netatmo app (client id): Netatmo allows one
    /// URL per app. Devices subscribe with the app's client, the URL secret and the home they show.
    /// </summary>
    public interface INetatmoWebhookHub
    {
        /// <summary>
        /// Subscribes a device and makes sure the app's webhook is registered. Registration problems don't throw: they
        /// show in <see cref="GetStatus"/>, and the device keeps polling. Dispose the result to unsubscribe.
        /// </summary>
        Task<IDisposable> SubscribeAsync(NetatmoWebhookSubscription subscription, CancellationToken cancellationToken);

        /// <summary>
        /// Queues a push for the app whose URL secret is <paramref name="secret"/>. False for an unknown secret. Returns
        /// at once: Netatmo deactivates a webhook that answers slowly.
        /// </summary>
        bool TryAccept(string secret, string body);

        /// <summary>Checks the registration again when the last check is old enough, and registers it when it is gone.</summary>
        Task RecheckAsync(string clientId, CancellationToken cancellationToken);

        NetatmoWebhookStatus GetStatus(string clientId);
    }

    public enum NetatmoWebhookState
    {
        /// <summary>No device of the app has a webhook URL.</summary>
        Off,
        Registering,
        /// <summary>Registered: Netatmo pushes events.</summary>
        Active,
        /// <summary>Another URL is registered for the app, for example Home Assistant's.</summary>
        Conflict,
        /// <summary>Netatmo refused the registration; it is tried again later.</summary>
        Failed,
        /// <summary>Netatmo stopped pushing, for example after failed deliveries; it is registered again later.</summary>
        Deactivated
    }

    public sealed record NetatmoWebhookStatus(NetatmoWebhookState State, string? Message)
    {
        public bool IsActive => State == NetatmoWebhookState.Active;
    }

    /// <param name="Api">The app's API client, used to register the webhook.</param>
    /// <param name="Secret">The URL secret: the last path segment of <paramref name="Url"/>.</param>
    /// <param name="Url">The full URL Netatmo pushes to.</param>
    /// <param name="Takeover">Replace another URL registered for the app.</param>
    /// <param name="HomeId">The home the device shows; pushes of other homes aren't passed to it.</param>
    /// <param name="Handler">Called for each push, one at a time, off the request.</param>
    public sealed record NetatmoWebhookSubscription(
        NetatmoApiClient Api,
        string Secret,
        string Url,
        bool Takeover,
        string? HomeId,
        Func<NetatmoWebhookEvent, CancellationToken, Task> Handler)
    {
        public string ClientId => Api.Auth.ClientId;
    }
}
