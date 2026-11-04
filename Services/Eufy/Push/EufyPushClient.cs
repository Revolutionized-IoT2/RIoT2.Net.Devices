// Ported from eufy-sdk src/client/eufy-mega.ts (startPush, retryPush, PUSH_RETRY_MIN_MS/PUSH_RETRY_MAX_MS) and
// src/transport/push/push-client.ts (the TLS dial) @ 5f92bea5 (Apache-2.0), modified for RIoT2.

using System.Net.Security;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace RIoT2.Net.Devices.Services.Eufy.Push
{
    /// <summary>Test seams of <see cref="EufyPushClient"/>.</summary>
    internal sealed class EufyPushClientHooks
    {
        /// <summary>Opens the MCS stream in place of the TLS dial to mtalk.google.com:5228.</summary>
        public Func<CancellationToken, Task<Stream>>? Connect { get; init; }
        /// <summary>The handler of the FCM registration requests.</summary>
        public HttpMessageHandler? HttpHandler { get; init; }
        /// <summary>Replaces every backoff wait.</summary>
        public Func<TimeSpan, CancellationToken, Task>? Delay { get; init; }
        public TimeSpan? HeartbeatInterval { get; init; }
        public IFcmStore? Store { get; init; }
    }

    /// <summary>
    /// The eufy FCM push channel: registers with FCM once (persisted in
    /// <c>&lt;DataDirectory&gt;/eufy-push-&lt;hash&gt;.json</c>), registers the token with the eufy cloud, holds
    /// the MCS connection and raises the decoded <see cref="EufySecurityEvent"/>s.
    /// </summary>
    /// <remarks>
    /// <see cref="StartAsync"/> returns once the first MCS login succeeds. If that bring-up fails it throws,
    /// and the client keeps retrying in the background (30 s, doubling up to 10 min, as upstream's
    /// <c>retryPush</c>) until <see cref="StopAsync"/>; <see cref="ConnectionChanged"/> reports when it comes up.
    /// Once up, a dropped connection reconnects with the MCS client's own backoff.
    /// </remarks>
    public sealed class EufyPushClient : IEufyPushClient
    {
        /// <summary>The first wait before bringing push up again after a failed bring-up; each following one doubles.</summary>
        internal static readonly TimeSpan PushRetryMin = TimeSpan.FromMilliseconds(30_000);
        internal static readonly TimeSpan PushRetryMax = TimeSpan.FromMilliseconds(600_000);

        private readonly IEufyCloudApi _cloud;
        private readonly ILogger _logger;
        private readonly IFcmStore _store;
        private readonly HttpClient _http;
        private readonly FcmRegistrar _registrar;
        private readonly Func<CancellationToken, Task<Stream>> _connect;
        private readonly Func<TimeSpan, CancellationToken, Task> _delay;
        private readonly TimeSpan? _heartbeatInterval;
        private readonly Lock _gate = new();
        private CancellationTokenSource? _stopCts;
        private Task? _loop;
        private TaskCompletionSource _connectWaiter = NewWaiter();
        private volatile bool _connected;
        private bool _disposed;

        public EufyPushClient(EufyAccountOptions options, IEufyCloudApi cloud, ILogger logger)
            : this(options, cloud, logger, null)
        {
        }

        internal EufyPushClient(EufyAccountOptions options, IEufyCloudApi cloud, ILogger logger, EufyPushClientHooks? hooks)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(cloud);
            ArgumentNullException.ThrowIfNull(logger);
            _cloud = cloud;
            _logger = logger;
            _store = hooks?.Store ?? FileFcmStore.ForAccount(options.DataDirectory, options.Email);
            _http = hooks?.HttpHandler is { } handler
                ? new HttpClient(handler, disposeHandler: false)
                : new HttpClient();
            _http.Timeout = TimeSpan.FromSeconds(30);
            _delay = hooks?.Delay ?? Task.Delay;
            _registrar = new FcmRegistrar(_http, logger, _delay);
            _connect = hooks?.Connect ?? ConnectTlsAsync;
            _heartbeatInterval = hooks?.HeartbeatInterval;
        }

        public event Action<EufySecurityEvent>? EventReceived;
        public event Action<bool>? ConnectionChanged;

        public bool IsConnected => _connected;

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            Task waiter;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_loop is null)
                {
                    _stopCts = new CancellationTokenSource();
                    var stop = _stopCts.Token;
                    _loop = Task.Run(() => SuperviseAsync(stop), CancellationToken.None);
                }
                if (_connected) return;
                waiter = _connectWaiter.Task;
            }
            try
            {
                await waiter.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await StopAsync().ConfigureAwait(false);
                throw;
            }
        }

        public async Task StopAsync()
        {
            CancellationTokenSource? cts;
            Task? loop;
            lock (_gate)
            {
                cts = _stopCts;
                loop = _loop;
                _stopCts = null;
                _loop = null;
            }
            if (cts is null || loop is null) return;
            await cts.CancelAsync().ConfigureAwait(false);
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[push] the push loop ended with an error");
            }
            cts.Dispose();
            TaskCompletionSource old;
            lock (_gate)
            {
                old = _connectWaiter;
                _connectWaiter = NewWaiter();
            }
            old.TrySetCanceled();
        }

        public async ValueTask DisposeAsync()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
            }
            await StopAsync().ConfigureAwait(false);
            _http.Dispose();
        }

        private static TaskCompletionSource NewWaiter() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Brings push up, and again after a failed bring-up with doubling backoff (upstream <c>retryPush</c>).</summary>
        private async Task SuperviseAsync(CancellationToken stop)
        {
            var retry = PushRetryMin;
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await BringUpAsync(stop).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[push] could not bring push up; retrying in {Delay}", retry);
                    TaskCompletionSource failed;
                    lock (_gate)
                    {
                        failed = _connectWaiter;
                        _connectWaiter = NewWaiter();
                    }
                    failed.TrySetException(ex);
                }
                try
                {
                    await _delay(retry, stop).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                retry = TimeSpan.FromTicks(Math.Min(retry.Ticks * 2, PushRetryMax.Ticks));
            }
        }

        /// <summary>Upstream <c>startPush</c>: credentials, token registration, then the MCS client (which only returns by throwing).</summary>
        private async Task BringUpAsync(CancellationToken stop)
        {
            if (_cloud.UserId is null) throw new InvalidOperationException("eufy push: log in to the cloud first");
            var persisted = await _store.LoadAsync(stop).ConfigureAwait(false);
            if (persisted is null)
            {
                _logger.LogInformation("[push] registering with FCM");
                var registered = await _registrar.RegisterAsync(stop).ConfigureAwait(false);
                persisted = new PersistedPush { Creds = registered, PersistentIds = [] };
                await _store.SaveAsync(persisted, stop).ConfigureAwait(false);
            }

            // Tell the eufy cloud to push this account's events to the token (best-effort: the MCS socket
            // still receives even if this call's exact shape drifts).
            try
            {
                await _cloud.RegisterPushTokenAsync(persisted.Creds.FcmToken, stop).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stop.IsCancellationRequested)
            {
                _logger.LogWarning("[push] registering the push token with the eufy cloud failed: {Error}", ex.Message);
            }

            var session = new Session(this, persisted.Creds);
            var client = new McsPushClient(persisted.Creds, _connect, session, _logger, _delay, _heartbeatInterval);
            session.Client = client;
            client.SetPersistentIds(persisted.PersistentIds);
            await client.RunAsync(stop).ConfigureAwait(false);
        }

        private static async Task<Stream> ConnectTlsAsync(CancellationToken cancellationToken)
        {
            var tcp = new TcpClient();
            try
            {
                await tcp.ConnectAsync(McsPushClient.Host, McsPushClient.Port, cancellationToken).ConfigureAwait(false);
                tcp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                // The stream owns the socket; disposing the SslStream closes the connection.
                var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
                try
                {
                    // TargetHost sets SNI and the name the (default, verified) certificate check matches: the login
                    // carries the securityToken, so the peer must be Google's.
                    await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = McsPushClient.Host }, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    await ssl.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
                return ssl;
            }
            catch
            {
                tcp.Dispose();
                throw;
            }
        }

        private void OnConnected()
        {
            TaskCompletionSource waiter;
            lock (_gate)
            {
                _connected = true;
                waiter = _connectWaiter;
            }
            waiter.TrySetResult();
            _logger.LogInformation("[push] connected");
            RaiseConnectionChanged(true);
        }

        private void OnDisconnected()
        {
            lock (_gate)
            {
                _connected = false;
                if (_connectWaiter.Task.IsCompleted) _connectWaiter = NewWaiter();
            }
            _logger.LogInformation("[push] disconnected");
            RaiseConnectionChanged(false);
        }

        private async Task OnPushAsync(PushEvent push, FcmCredentials creds, McsPushClient? client, CancellationToken cancellationToken)
        {
            if (push.EventName is null && push.EventType is { } type) push.EventName = PushEvents.DetectionName(type);
            var decoded = PushEventDecoder.Decode(push);
            _logger.LogDebug("[push] in: eventType={EventType} ({EventName}) event={Event}", push.EventType, push.EventName, decoded?.Kind.ToString() ?? "none");
            if (decoded is not null) RaiseEventReceived(decoded);
            if (client is not null) await SaveIdsAsync(creds, client, cancellationToken).ConfigureAwait(false);
        }

        private async Task SaveIdsAsync(FcmCredentials creds, McsPushClient client, CancellationToken cancellationToken)
        {
            try
            {
                await _store.SaveAsync(new PersistedPush { Creds = creds, PersistentIds = client.GetPersistentIds() }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("[push] could not save the push state: {Error}", ex.Message);
            }
        }

        private void RaiseEventReceived(EufySecurityEvent e)
        {
            if (EventReceived is not { } handlers) return;
            foreach (var handler in handlers.GetInvocationList().Cast<Action<EufySecurityEvent>>())
            {
                try
                {
                    handler(e);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[push] an EventReceived subscriber failed");
                }
            }
        }

        private void RaiseConnectionChanged(bool connected)
        {
            if (ConnectionChanged is not { } handlers) return;
            foreach (var handler in handlers.GetInvocationList().Cast<Action<bool>>())
            {
                try
                {
                    handler(connected);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[push] a ConnectionChanged subscriber failed");
                }
            }
        }

        /// <summary>One bring-up's view of the MCS client's callbacks.</summary>
        private sealed class Session(EufyPushClient owner, FcmCredentials creds) : IMcsPushHandler
        {
            public McsPushClient? Client { get; set; }

            public async Task OnConnectedAsync(CancellationToken cancellationToken)
            {
                // The login acknowledged the reported ids; persist the trimmed list.
                if (Client is not null) await owner.SaveIdsAsync(creds, Client, cancellationToken).ConfigureAwait(false);
                owner.OnConnected();
            }

            public void OnDisconnected() => owner.OnDisconnected();

            public Task OnPushAsync(PushEvent push, CancellationToken cancellationToken) => owner.OnPushAsync(push, creds, Client, cancellationToken);

            public void OnError(Exception error) => owner._logger.LogDebug(error, "[push] connection error; reconnecting");
        }
    }
}
