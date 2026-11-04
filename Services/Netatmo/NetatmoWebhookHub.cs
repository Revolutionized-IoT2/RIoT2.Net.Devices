using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using RIoT2.Net.Devices.Models;
using RIoT2.Net.Devices.Services.Interfaces;

namespace RIoT2.Net.Devices.Services.Netatmo
{
    /// <summary>
    /// <see cref="INetatmoWebhookHub"/>. The webhook URL is the only credential (Netatmo signs nothing that is
    /// documented), so it is never logged: logs name the host at most.
    /// </summary>
    public sealed class NetatmoWebhookHub : INetatmoWebhookHub, IDisposable
    {
        internal const string WebhookPath = "webhooks/v1";
        internal static readonly TimeSpan RecheckInterval = TimeSpan.FromMinutes(30);
        internal static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(30);
        internal static readonly TimeSpan DeactivatedWait = TimeSpan.FromHours(1);

        private readonly ILogger _logger;
        private readonly Func<DateTimeOffset> _now;
        private readonly Channel<(App App, string Body)> _queue;
        private readonly CancellationTokenSource _stop = new();
        private readonly object _lock = new();
        private readonly Dictionary<string, App> _apps = new(StringComparer.Ordinal);
        private Task? _reader;

        public NetatmoWebhookHub(ILogger logger) : this(logger, () => DateTimeOffset.UtcNow) { }

        internal NetatmoWebhookHub(ILogger logger, Func<DateTimeOffset> now)
        {
            _logger = logger;
            _now = now;
            // A burst beyond this drops the oldest pushes; the devices' polling catches up with them.
            _queue = Channel.CreateBounded<(App, string)>(new BoundedChannelOptions(256)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true
            }, _ => Interlocked.Decrement(ref _pending));
        }

        /// <summary>Completes when the queued pushes are handled; for tests.</summary>
        internal async Task DrainAsync()
        {
            while (Volatile.Read(ref _pending) > 0)
                await Task.Delay(10).ConfigureAwait(false);
        }

        // Pushes accepted and not handled yet.
        private int _pending;

        public async Task<IDisposable> SubscribeAsync(NetatmoWebhookSubscription subscription, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(subscription);
            App app;
            var subscriber = new Subscriber(subscription);
            lock (_lock)
            {
                // An app's state (a deactivation, a backoff) outlives a device restart; a new URL starts afresh.
                if (!_apps.TryGetValue(subscription.ClientId, out app!) || (app.Subscribers.Count == 0 && app.Url != subscription.Url))
                {
                    app = new App(subscription.ClientId, subscription.Secret, subscription.Url);
                    _apps[subscription.ClientId] = app;
                }
                else if (app.Url != subscription.Url)
                {
                    _logger.LogWarning("Netatmo app {ClientId} has devices with different webhook URLs; Netatmo allows one per app. The first one is used.", subscription.ClientId);
                }
                app.Subscribers.Add(subscriber);
                _reader ??= Task.Run(ReadAsync);
            }

            try
            {
                await RegisterAsync(app, force: true, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                Unsubscribe(app, subscriber);
                throw;
            }
            return new Unsubscriber(this, app, subscriber);
        }

        public bool TryAccept(string secret, string body)
        {
            if (string.IsNullOrEmpty(secret))
                return false;
            var presented = Encoding.UTF8.GetBytes(secret);
            App? match = null;
            lock (_lock)
            {
                foreach (var app in _apps.Values)
                {
                    if (CryptographicOperations.FixedTimeEquals(presented, app.SecretBytes))
                        match = app;
                }
            }
            if (match == null)
                return false;
            Interlocked.Increment(ref _pending);
            if (!_queue.Writer.TryWrite((match, body)))
                Interlocked.Decrement(ref _pending);
            return true;
        }

        public Task RecheckAsync(string clientId, CancellationToken cancellationToken)
        {
            App? app;
            lock (_lock)
                _apps.TryGetValue(clientId, out app);
            return app == null ? Task.CompletedTask : RegisterAsync(app, force: false, cancellationToken);
        }

        public NetatmoWebhookStatus GetStatus(string clientId)
        {
            lock (_lock)
                return _apps.TryGetValue(clientId, out var app) ? app.Status : new NetatmoWebhookStatus(NetatmoWebhookState.Off, null);
        }

        private void Unsubscribe(App app, Subscriber subscriber)
        {
            // The app stays, with its state, and so does the registration at Netatmo: a configuration change restarts
            // devices, and registering again each time would run into Netatmo's limits. Pushes that arrive meanwhile
            // are accepted, so Netatmo doesn't count them as failed deliveries.
            lock (_lock)
                app.Subscribers.Remove(subscriber);
        }

        private async Task RegisterAsync(App app, bool force, CancellationToken cancellationToken)
        {
            await app.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var now = _now();
                // A new subscriber checks again unless the app is known to work or waits on purpose (a deactivation
                // or a backoff); a conflict may have been resolved, or webhookTakeover set.
                if (now < app.NextAttempt && !(force && app.Status.State is NetatmoWebhookState.Off or NetatmoWebhookState.Registering or NetatmoWebhookState.Conflict))
                    return;

                NetatmoWebhookSubscription? subscription;
                bool takeover;
                lock (_lock)
                {
                    subscription = app.Subscribers.FirstOrDefault()?.Subscription;
                    takeover = app.Subscribers.Any(s => s.Subscription.Takeover);
                }
                if (subscription == null)
                    return;

                var api = subscription.Api;
                var host = Host(app.Url);
                if (app.Status.State == NetatmoWebhookState.Off)
                    SetStatus(app, NetatmoWebhookState.Registering, null);

                try
                {
                    var registered = (await api.GetAsync<NetatmoWebhookList>(WebhookPath, null, cancellationToken).ConfigureAwait(false))?.body?
                        .Select(entry => entry.url).Where(url => !string.IsNullOrEmpty(url)).ToList() ?? [];
                    var ours = registered.Contains(app.Url, StringComparer.Ordinal);
                    // A URL under the same base is this install's, with an earlier secret: replace it.
                    var earlier = registered.Count > 0 && registered.All(url => url!.StartsWith(Base(app.Url), StringComparison.OrdinalIgnoreCase));

                    if (ours && app.Status.State != NetatmoWebhookState.Deactivated)
                    {
                        Registered(app, now);
                        return;
                    }

                    if (registered.Count > 0)
                    {
                        if (!ours && !earlier && !takeover)
                        {
                            Conflict(app, now);
                            return;
                        }
                        // Ours after a deactivation, an earlier secret, or another URL we may replace: one per app.
                        await api.DeleteAsync(WebhookPath, cancellationToken).ConfigureAwait(false);
                    }

                    await api.PostJsonAsync<object>(WebhookPath, new { url = app.Url }, cancellationToken).ConfigureAwait(false);
                    _logger.LogInformation("Registered the Netatmo webhook of app {ClientId} at {Host}.", app.ClientId, host);
                    Registered(app, now);
                }
                catch (NetatmoApiException error) when (error.Status == HttpStatusCode.Conflict || error.TextCode == "WH009")
                {
                    Conflict(app, now);
                }
                catch (NetatmoNotConnectedException)
                {
                    // Registered once the device is connected: the next check tries again.
                    SetStatus(app, NetatmoWebhookState.Registering, "Waiting for the device to be connected to Netatmo.");
                    app.NextAttempt = now;
                }
                catch (NetatmoApiException error)
                {
                    app.Backoff = app.Backoff == TimeSpan.Zero ? TimeSpan.FromMinutes(1) : TimeSpan.FromTicks(Math.Min(app.Backoff.Ticks * 2, MaxBackoff.Ticks));
                    var invalidUrl = error.Code == 21 || (error.Status == HttpStatusCode.BadRequest && !error.IsThrottled);
                    var message = invalidUrl
                        ? $"Netatmo refused the webhook URL at {host}: it must be a public HTTPS address."
                        : $"Could not register the Netatmo webhook: {error.Message}";
                    _logger.LogWarning("Could not register the Netatmo webhook of app {ClientId} at {Host}: {Error}", app.ClientId, host, error.Message);
                    SetStatus(app, NetatmoWebhookState.Failed, message);
                    app.NextAttempt = now + (invalidUrl ? RecheckInterval : app.Backoff);
                }
            }
            finally
            {
                app.Gate.Release();
            }
        }

        private void Registered(App app, DateTimeOffset now)
        {
            SetStatus(app, NetatmoWebhookState.Active, null);
            app.Backoff = TimeSpan.Zero;
            app.NextAttempt = now + RecheckInterval;
        }

        private void Conflict(App app, DateTimeOffset now)
        {
            _logger.LogWarning("Another webhook is registered for Netatmo app {ClientId}. Use a separate Netatmo app for RIoT2, or set webhookTakeover.", app.ClientId);
            SetStatus(app, NetatmoWebhookState.Conflict,
                "Another webhook (for example Home Assistant's) is registered for this Netatmo app. Use a separate Netatmo app for RIoT2, or set webhookTakeover to replace it.");
            app.NextAttempt = now + RecheckInterval;
        }

        private void SetStatus(App app, NetatmoWebhookState state, string? message)
        {
            lock (_lock)
                app.Status = new NetatmoWebhookStatus(state, message);
        }

        private async Task ReadAsync()
        {
            try
            {
                await foreach (var (app, body) in _queue.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
                {
                    try
                    {
                        await HandleAsync(app, body).ConfigureAwait(false);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _pending);
                    }
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                // Disposed.
            }
        }

        private async Task HandleAsync(App app, string body)
        {
            var pushed = NetatmoWebhookEvent.Parse(body);
            if (pushed == null)
            {
                _logger.LogDebug("Ignored a Netatmo webhook push for app {ClientId} that isn't a JSON object.", app.ClientId);
                return;
            }

            if (pushed.IsActivation)
            {
                Registered(app, _now());
                _logger.LogInformation("Netatmo activated the webhook of app {ClientId}.", app.ClientId);
            }
            else if (pushed.IsDeactivation)
            {
                SetStatus(app, NetatmoWebhookState.Deactivated, "Netatmo stopped the webhook, for example after failed deliveries. It is registered again later; until then the device polls.");
                app.NextAttempt = _now() + DeactivatedWait;
                _logger.LogWarning("Netatmo deactivated the webhook of app {ClientId}.", app.ClientId);
            }

            List<Subscriber> subscribers;
            lock (_lock)
                subscribers = app.Subscribers.ToList();

            foreach (var subscriber in subscribers)
            {
                var home = subscriber.Subscription.HomeId;
                if (!string.IsNullOrEmpty(home) && !string.IsNullOrEmpty(pushed.HomeId) && home != pushed.HomeId)
                    continue;
                try
                {
                    await subscriber.Subscription.Handler(pushed, _stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception error)
                {
                    _logger.LogError(error, "A Netatmo device failed to handle a webhook push ({EventType}).", pushed.EventType ?? pushed.PushType);
                }
            }
        }

        private static string Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "?";

        // The URL without its secret: "https://host/api/netatmo/webhook/".
        private static string Base(string url) => url[..(url.LastIndexOf('/') + 1)];

        public void Dispose()
        {
            _stop.Cancel();
            _queue.Writer.TryComplete();
        }

        private sealed class App(string clientId, string secret, string url)
        {
            public string ClientId { get; } = clientId;
            public string Url { get; } = url;
            public byte[] SecretBytes { get; } = Encoding.UTF8.GetBytes(secret);
            public SemaphoreSlim Gate { get; } = new(1, 1);
            public List<Subscriber> Subscribers { get; } = [];
            public NetatmoWebhookStatus Status { get; set; } = new(NetatmoWebhookState.Off, null);
            public DateTimeOffset NextAttempt { get; set; }
            public TimeSpan Backoff { get; set; }
        }

        private sealed class Subscriber(NetatmoWebhookSubscription subscription)
        {
            public NetatmoWebhookSubscription Subscription { get; } = subscription;
        }

        private sealed class Unsubscriber(NetatmoWebhookHub hub, App app, Subscriber subscriber) : IDisposable
        {
            private int _disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                    hub.Unsubscribe(app, subscriber);
            }
        }
    }
}
