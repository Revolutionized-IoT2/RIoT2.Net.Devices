using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using RIoT2.Net.Devices.Services.Eufy;
using RIoT2.Net.Devices.Services.Interfaces;

namespace RIoT2.Net.Devices.Services
{
    /// <summary>Creates the eufy clients; tests replace it.</summary>
    internal interface IEufyComponentFactory
    {
        IEufyCloudApi CreateCloud(EufyAccountOptions options, ILogger logger);
        IEufyPushClient CreatePush(EufyAccountOptions options, IEufyCloudApi cloud, ILogger logger);
        IEufyP2PClient CreateP2P(EufyAccountOptions options, IEufyCloudApi cloud, ILogger logger);
    }

    internal sealed class EufyComponentFactory : IEufyComponentFactory
    {
        public static readonly EufyComponentFactory Default = new();

        public IEufyCloudApi CreateCloud(EufyAccountOptions options, ILogger logger) => new Eufy.Cloud.EufyCloudClient(options, logger);
        public IEufyPushClient CreatePush(EufyAccountOptions options, IEufyCloudApi cloud, ILogger logger) => new Eufy.Push.EufyPushClient(options, cloud, logger);
        public IEufyP2PClient CreateP2P(EufyAccountOptions options, IEufyCloudApi cloud, ILogger logger) => new Eufy.P2P.EufyP2PClient(options, cloud, logger);
    }

    /// <summary>
    /// The eufy account, without the eufy-security-ws container: the cloud login and device list, push events over FCM
    /// and station commands over P2P (a C# port of eufy-sdk, see <c>Services/Eufy</c>).
    /// </summary>
    /// <remarks>
    /// Push events go through a bounded queue to one worker, which downloads their thumbnails and raises them in arrival
    /// order; when the queue is full the oldest event is dropped and logged. A failing event is logged and the next one
    /// is still handled.
    /// </remarks>
    public sealed class EufySecurityService : IEufySecurityService, IAsyncDisposable
    {
        /// <summary>Push events that may wait for the worker before the oldest one is dropped.</summary>
        public const int EventQueueCapacity = 1000;
        /// <summary>The station param that holds the guard mode.</summary>
        public const int GuardModeParam = 1224;

        private static readonly TimeSpan DefaultArmingRefreshDelay = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan PushRetryMin = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan PushRetryMax = TimeSpan.FromMinutes(5);

        private readonly ILogger _logger;
        private readonly IEufyComponentFactory _factory;
        private readonly TimeSpan _armingRefreshDelay;
        // One login or connect step at a time.
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly ConcurrentDictionary<string, int> _guardModes = new();
        private EufyAccountOptions? _options;
        private IEufyCloudApi? _cloud;
        private IEufyPushClient? _push;
        private IEufyP2PClient? _p2p;
        private CancellationTokenSource? _shutdownCts;
        private Channel<EufySecurityEvent>? _events;
        private Task _worker = Task.CompletedTask;
        private Task _pushStarter = Task.CompletedTask;
        private volatile IReadOnlyList<EufyDeviceRecord> _devices = [];
        private volatile EufyServiceStatus _status = new(EufyConnectionState.Stopped);

        public EufySecurityService(ILogger<EufySecurityService> logger) : this(logger, EufyComponentFactory.Default, DefaultArmingRefreshDelay) { }

        internal EufySecurityService(ILogger logger, IEufyComponentFactory factory, TimeSpan armingRefreshDelay)
        {
            _logger = logger;
            _factory = factory;
            _armingRefreshDelay = armingRefreshDelay;
        }

        public event Action<EufySecurityEvent, byte[]?>? SecurityEvent;
        public event Action<string, int>? GuardModeChanged;
        public event Action<EufyServiceStatus>? StatusChanged;

        public EufyServiceStatus Status => _status;
        public IReadOnlyList<EufyDeviceRecord> Devices => _devices;

        public void Configure(EufyAccountOptions options) => _options = options;

        public int? GetGuardMode(string stationSerial) => _guardModes.TryGetValue(stationSerial, out var mode) ? mode : null;

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            var options = _options ?? throw new InvalidOperationException("Configure the Eufy Security service before starting it.");

            // A new run each start: the service is shared and its device may be restarted.
            await StopAsync().ConfigureAwait(false);
            _shutdownCts = new CancellationTokenSource();
            var events = Channel.CreateBounded<EufySecurityEvent>(new BoundedChannelOptions(EventQueueCapacity)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.DropOldest
            }, _ => _logger.LogWarning("Eufy event queue full ({Capacity}); dropped the oldest event.", EventQueueCapacity));
            _events = events;
            var shutdown = _shutdownCts.Token;
            _worker = Task.Run(() => ProcessEventsAsync(events.Reader, shutdown), CancellationToken.None);

            ExceptionDispatchInfo? failure = null;
            using (var start = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown))
            {
                await _gate.WaitAsync(start.Token).ConfigureAwait(false);
                try
                {
                    SetStatus(new EufyServiceStatus(EufyConnectionState.Connecting));
                    _cloud = _factory.CreateCloud(options, _logger);
                    var result = await _cloud.LoginAsync(start.Token).ConfigureAwait(false);
                    await HandleLoginResultAsync(result, start.Token).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    failure = ExceptionDispatchInfo.Capture(error);
                }
                finally
                {
                    _gate.Release();
                }
            }

            if (failure == null)
                return;

            // Nothing of a failed start keeps running.
            await StopAsync().ConfigureAwait(false);
            if (failure.SourceException is not OperationCanceledException)
                SetStatus(new EufyServiceStatus(EufyConnectionState.Error, failure.SourceException.Message));
            failure.Throw();
        }

        public async Task SubmitVerificationAsync(string answer, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(answer))
                throw new ArgumentException("The answer is empty.", nameof(answer));

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var cloud = _cloud;
                var state = _status.State;
                if (cloud == null || state is not (EufyConnectionState.CaptchaRequired or EufyConnectionState.TwoFactorRequired))
                    throw new InvalidOperationException("No eufy captcha or verification code is pending.");

                var result = state == EufyConnectionState.CaptchaRequired
                    ? await cloud.SolveCaptchaAsync(answer.Trim(), cancellationToken).ConfigureAwait(false)
                    : await cloud.SubmitVerifyCodeAsync(answer.Trim(), cancellationToken).ConfigureAwait(false);
                await HandleLoginResultAsync(result, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not (OperationCanceledException or InvalidOperationException))
            {
                SetStatus(new EufyServiceStatus(EufyConnectionState.Error, error.Message));
                throw;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task RefreshAsync(CancellationToken cancellationToken)
        {
            var cloud = _cloud;
            if (cloud == null || _status.State != EufyConnectionState.Connected)
                return;

            IReadOnlyList<EufyDeviceRecord> devices;
            try
            {
                devices = await cloud.GetDevicesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (EufySessionExpiredException error)
            {
                SetStatus(new EufyServiceStatus(EufyConnectionState.Error, "The eufy session ended and could not be renewed; restart the device to log in again. " + error.Message));
                throw;
            }

            ApplyDevices(devices);
        }

        public async Task SetGuardModeAsync(string stationSerial, int mode, CancellationToken cancellationToken)
        {
            var p2p = ConnectedP2P();
            await p2p.SetGuardModeAsync(stationSerial, mode, cancellationToken).ConfigureAwait(false);
            // The station acknowledged the write; the cloud's copy catches up later.
            SetGuardMode(stationSerial, mode);
        }

        public Task<byte[]> SnapshotAsync(string deviceSerial, CancellationToken cancellationToken) =>
            ConnectedP2P().SnapshotAsync(deviceSerial, cancellationToken);

        public async Task StopAsync()
        {
            _shutdownCts?.Cancel();
            var push = _push;
            var p2p = _p2p;
            var cloud = _cloud;
            _push = null;
            _p2p = null;
            _cloud = null;

            await IgnoreFailureAsync(_pushStarter, "starting the push channel").ConfigureAwait(false);
            if (push != null)
            {
                push.EventReceived -= OnPushEvent;
                push.ConnectionChanged -= OnPushConnectionChanged;
                await IgnoreFailureAsync(push.StopAsync(), "stopping the push channel").ConfigureAwait(false);
                await IgnoreFailureAsync(push.DisposeAsync().AsTask(), "disposing the push channel").ConfigureAwait(false);
            }

            if (p2p != null)
            {
                await IgnoreFailureAsync(p2p.CloseAsync(), "closing the P2P sessions").ConfigureAwait(false);
                await IgnoreFailureAsync(p2p.DisposeAsync().AsTask(), "disposing the P2P client").ConfigureAwait(false);
            }

            cloud?.Dispose();
            _events?.Writer.TryComplete();
            await IgnoreFailureAsync(_worker, "handling the eufy events").ConfigureAwait(false);
            _shutdownCts?.Dispose();
            _shutdownCts = null;
            _events = null;
            _devices = [];
            _guardModes.Clear();
            if (_status.State != EufyConnectionState.Stopped)
                SetStatus(new EufyServiceStatus(EufyConnectionState.Stopped));
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync().ConfigureAwait(false);
            _gate.Dispose();
        }

        internal void EnqueueEvent(EufySecurityEvent securityEvent)
        {
            var events = _events;
            if (events == null || !events.Writer.TryWrite(securityEvent))
                _logger.LogDebug("Eufy event received while the service is stopped; ignored.");
        }

        private async Task HandleLoginResultAsync(EufyLoginResult result, CancellationToken cancellationToken)
        {
            switch (result.Status)
            {
                case EufyLoginStatus.Captcha:
                    _logger.LogWarning("The eufy cloud asks for a captcha; answer it with the device's verify command.");
                    SetStatus(new EufyServiceStatus(EufyConnectionState.CaptchaRequired,
                        result.Retry ? "The captcha answer was wrong; answer the new captcha." : "Answer the captcha with the verify command.",
                        CaptchaImage: result.CaptchaImage));
                    break;
                case EufyLoginStatus.TwoFactor:
                    _logger.LogWarning("The eufy cloud sent a verification code ({Method}); answer it with the device's verify command.", result.TwoFactorMethod);
                    SetStatus(new EufyServiceStatus(EufyConnectionState.TwoFactorRequired,
                        "Enter the verification code with the verify command.", TwoFactorMethod: result.TwoFactorMethod));
                    break;
                default:
                    await ConnectAsync(cancellationToken).ConfigureAwait(false);
                    break;
            }
        }

        private async Task ConnectAsync(CancellationToken cancellationToken)
        {
            var options = _options!;
            var cloud = _cloud!;
            var devices = await cloud.GetDevicesAsync(cancellationToken).ConfigureAwait(false);

            _p2p = _factory.CreateP2P(options, cloud, _logger);
            ApplyDevices(devices);

            var push = _factory.CreatePush(options, cloud, _logger);
            push.EventReceived += OnPushEvent;
            push.ConnectionChanged += OnPushConnectionChanged;
            _push = push;
            _pushStarter = StartPushAsync(push, _shutdownCts!.Token);

            _logger.LogInformation("Connected to the eufy cloud: {Stations} station(s), {Devices} device(s).",
                devices.Count(d => d.IsStation), devices.Count(d => !d.IsStation));
            SetStatus(new EufyServiceStatus(EufyConnectionState.Connected));
        }

        // The push channel reconnects on its own once it has connected; until then, retry here.
        private async Task StartPushAsync(IEufyPushClient push, CancellationToken cancellationToken)
        {
            var delay = PushRetryMin;
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await push.StartAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception error)
                {
                    _logger.LogWarning(error, "The eufy push channel did not start; events don't arrive until it does. Trying again in {Delay}.", delay);
                }

                try
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, PushRetryMax.Ticks));
            }
        }

        private void ApplyDevices(IReadOnlyList<EufyDeviceRecord> devices)
        {
            _devices = devices;
            _p2p?.UpdateDevices(devices);
            foreach (var station in devices.Where(d => d.IsStation))
            {
                if (station.Params.TryGetValue(GuardModeParam, out var raw) &&
                    int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mode))
                    SetGuardMode(station.Serial, mode);
            }
        }

        private void SetGuardMode(string stationSerial, int mode)
        {
            var changed = true;
            _guardModes.AddOrUpdate(stationSerial, mode, (_, previous) =>
            {
                changed = previous != mode;
                return mode;
            });

            if (changed)
                Raise(() => GuardModeChanged?.Invoke(stationSerial, mode), "guard mode");
        }

        private void OnPushEvent(EufySecurityEvent securityEvent) => EnqueueEvent(securityEvent);

        private void OnPushConnectionChanged(bool connected)
        {
            if (connected)
                _logger.LogInformation("The eufy push channel is connected.");
            else
                _logger.LogWarning("The eufy push channel disconnected; it reconnects on its own.");
        }

        private async Task ProcessEventsAsync(ChannelReader<EufySecurityEvent> reader, CancellationToken cancellationToken)
        {
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (reader.TryRead(out var securityEvent))
                {
                    try
                    {
                        await HandleEventAsync(securityEvent, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        // Stopping: the remaining events are drained without handling.
                    }
                    catch (Exception error)
                    {
                        _logger.LogError(error, "Error handling a eufy {Kind} event; the next events are still handled.", securityEvent.Kind);
                    }
                }
            }
        }

        private async Task HandleEventAsync(EufySecurityEvent securityEvent, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (securityEvent.Kind == EufyEventKind.ArmingModeChanged)
            {
                // The push carries no mode: give the cloud a moment to store it, then read it.
                await Task.Delay(_armingRefreshDelay, cancellationToken).ConfigureAwait(false);
                await RefreshAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            byte[]? picture = null;
            var cloud = _cloud;
            if (!string.IsNullOrEmpty(securityEvent.ThumbnailUrl) && cloud != null)
            {
                try
                {
                    picture = await cloud.DownloadImageAsync(securityEvent.ThumbnailUrl, P2pDidOf(securityEvent), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    _logger.LogWarning(error, "Could not download the eufy {Kind} thumbnail; the event is reported without it.", securityEvent.Kind);
                }
            }

            Raise(() => SecurityEvent?.Invoke(securityEvent, picture), "security");
        }

        private string? P2pDidOf(EufySecurityEvent securityEvent)
        {
            var devices = _devices;
            var stationSerial = securityEvent.StationSerial
                ?? devices.FirstOrDefault(d => d.Serial == securityEvent.DeviceSerial)?.StationSerial;
            return devices.FirstOrDefault(d => d.Serial == stationSerial)?.P2pDid
                ?? devices.FirstOrDefault(d => d.Serial == securityEvent.DeviceSerial)?.P2pDid;
        }

        private IEufyP2PClient ConnectedP2P()
        {
            var p2p = _p2p;
            if (p2p == null || _status.State != EufyConnectionState.Connected)
                throw new InvalidOperationException($"The eufy account is not connected ({_status.State}).");
            return p2p;
        }

        private void SetStatus(EufyServiceStatus status)
        {
            _status = status;
            Raise(() => StatusChanged?.Invoke(status), "status");
        }

        // A failing subscriber must not break the caller.
        private void Raise(Action raise, string kind)
        {
            try
            {
                raise();
            }
            catch (Exception error)
            {
                _logger.LogError(error, "A eufy {Kind} event handler failed.", kind);
            }
        }

        private async Task IgnoreFailureAsync(Task task, string what)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception error)
            {
                _logger.LogDebug(error, "Eufy: {What} failed while stopping.", what);
            }
        }
    }
}
