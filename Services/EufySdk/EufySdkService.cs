using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using RIoT2.Net.Devices.Services.Eufy;
using RIoT2.Net.Devices.Services.Interfaces;

namespace RIoT2.Net.Devices.Services.EufySdk
{
    /// <summary>
    /// The eufy account through eufy-sdk itself (@mega-yfue/eufy-sdk): a Node.js child process runs the embedded bridge
    /// (<c>EufySdkBridge/</c>) and this service talks to it over stdin/stdout. The process is restarted with a growing
    /// delay when it exits on its own; eufy-sdk restores the saved session, so a restart doesn't log in again.
    /// </summary>
    /// <remarks>
    /// Events go through a bounded queue to one worker, which raises them in arrival order; when the queue is full the
    /// oldest event is dropped and logged. A failing event is logged and the next one is still handled.
    /// </remarks>
    public sealed class EufySdkService : IEufySdkService, IAsyncDisposable
    {
        /// <summary>Events that may wait for the worker before the oldest one is dropped.</summary>
        public const int EventQueueCapacity = 1000;
        /// <summary>The station param that holds the guard mode.</summary>
        public const int GuardModeParam = 1224;

        private static readonly HashSet<int> GuardModes = [0, 1, 2, 3, 4, 5, 6, 47, 63];
        private static readonly TimeSpan DefaultArmingRefreshDelay = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan DefaultRestartDelay = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan RestartDelayMax = TimeSpan.FromMinutes(5);
        // A bridge that ran this long before it stopped restarts after the shortest delay again.
        private static readonly TimeSpan StableRun = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan QuickTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan LoginTimeout = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan SnapshotTimeout = TimeSpan.FromSeconds(90);

        /// <summary>eufy-sdk's semantic event names (its DeviceEventMap) and the kinds the devices report.</summary>
        internal static readonly IReadOnlyDictionary<string, EufyEventKind> EventKinds = new Dictionary<string, EufyEventKind>
        {
            ["motion"] = EufyEventKind.Motion,
            ["personDetected"] = EufyEventKind.PersonDetected,
            ["strangerDetected"] = EufyEventKind.StrangerDetected,
            ["petDetection"] = EufyEventKind.PetDetected,
            ["dogDetected"] = EufyEventKind.DogDetected,
            ["soundDetected"] = EufyEventKind.SoundDetected,
            ["cryingDetected"] = EufyEventKind.CryingDetected,
            ["vehicleDetected"] = EufyEventKind.VehicleDetected,
            ["doorbellPress"] = EufyEventKind.DoorbellPress,
            ["packageDelivered"] = EufyEventKind.PackageDelivered,
            ["packageTaken"] = EufyEventKind.PackageTaken,
            ["packageStranded"] = EufyEventKind.PackageStranded,
            ["armingModeChanged"] = EufyEventKind.ArmingModeChanged,
            ["alarm"] = EufyEventKind.Alarm
        };

        private readonly ILogger _logger;
        private readonly IEufyBridgeLauncher _launcher;
        private readonly TimeSpan _armingRefreshDelay;
        private readonly TimeSpan _restartDelay;
        private readonly TimeProvider _time;
        // One login, connect or restart step at a time.
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly ConcurrentDictionary<string, int> _guardModes = new();
        private EufyAccountOptions? _options;
        private EufyBridgeClient? _bridge;
        private DateTimeOffset _bridgeStarted;
        private CancellationTokenSource? _shutdownCts;
        private Channel<(EufySecurityEvent Event, byte[]? Picture)>? _events;
        private Task _worker = Task.CompletedTask;
        private Task _supervisor = Task.CompletedTask;
        private volatile IReadOnlyList<EufyDeviceRecord> _devices = [];
        private volatile EufyServiceStatus _status = new(EufyConnectionState.Stopped);

        public EufySdkService(ILogger<EufySdkService> logger)
            : this(logger, new NodeBridgeLauncher(logger), DefaultArmingRefreshDelay, DefaultRestartDelay, TimeProvider.System) { }

        internal EufySdkService(ILogger logger, IEufyBridgeLauncher launcher, TimeSpan armingRefreshDelay, TimeSpan restartDelay, TimeProvider time)
        {
            _logger = logger;
            _launcher = launcher;
            _armingRefreshDelay = armingRefreshDelay;
            _restartDelay = restartDelay;
            _time = time;
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
            _ = _options ?? throw new InvalidOperationException("Configure the eufy-sdk service before starting it.");

            // A new run each start: the service is shared and its device may be restarted.
            await StopAsync().ConfigureAwait(false);
            _shutdownCts = new CancellationTokenSource();
            var shutdown = _shutdownCts.Token;
            var events = Channel.CreateBounded<(EufySecurityEvent, byte[]?)>(new BoundedChannelOptions(EventQueueCapacity)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.DropOldest
            }, _ => _logger.LogWarning("eufy-sdk event queue full ({Capacity}); dropped the oldest event.", EventQueueCapacity));
            _events = events;
            _worker = Task.Run(() => ProcessEventsAsync(events.Reader, shutdown), CancellationToken.None);

            ExceptionDispatchInfo? failure = null;
            using (var start = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown))
            {
                await _gate.WaitAsync(start.Token).ConfigureAwait(false);
                try
                {
                    await LaunchAndLoginAsync(start.Token).ConfigureAwait(false);
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
            {
                _supervisor = Task.Run(() => SuperviseAsync(shutdown), CancellationToken.None);
                return;
            }

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
                var state = _status.State;
                if (_bridge == null || state is not (EufyConnectionState.CaptchaRequired or EufyConnectionState.TwoFactorRequired))
                    throw new InvalidOperationException("No eufy captcha or verification code is pending.");

                var result = state == EufyConnectionState.CaptchaRequired
                    ? await InvokeAsync("solveCaptcha", new { answer = answer.Trim() }, LoginTimeout, cancellationToken).ConfigureAwait(false)
                    : await InvokeAsync("submitVerifyCode", new { code = answer.Trim() }, LoginTimeout, cancellationToken).ConfigureAwait(false);
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
            if (_bridge == null || _status.State != EufyConnectionState.Connected)
                return;

            var result = await InvokeAsync("getDevices", null, ConnectTimeout, cancellationToken).ConfigureAwait(false);
            ApplyDevices(result);
        }

        public async Task SetGuardModeAsync(string stationSerial, int mode, CancellationToken cancellationToken)
        {
            if (!GuardModes.Contains(mode))
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "The eufy guard mode is 0 away, 1 home, 2 schedule, 3-5 custom, 6 off, 47 geofence or 63 disarmed.");

            EnsureConnected();
            await InvokeAsync("setGuardMode", new { stationSn = stationSerial, mode }, CommandTimeout, cancellationToken).ConfigureAwait(false);
            // eufy-sdk sent the write; its property announcement and the cloud's copy follow.
            SetGuardMode(stationSerial, mode);
        }

        public async Task<byte[]> SnapshotAsync(string deviceSerial, CancellationToken cancellationToken)
        {
            EnsureConnected();
            var result = await InvokeAsync("snapshot", new { sn = deviceSerial }, SnapshotTimeout, cancellationToken).ConfigureAwait(false);
            if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("jpeg", out var jpeg) || jpeg.GetString() is not { Length: > 0 } base64)
                throw new EufySdkException($"eufy-sdk returned no snapshot for {deviceSerial}.");
            if (result.TryGetProperty("retained", out var retained) && retained.ValueKind == JsonValueKind.True)
                _logger.LogInformation("eufy-sdk could not take a live still of {Device}; the last push thumbnail is reported instead.", deviceSerial);
            return Convert.FromBase64String(base64);
        }

        public async Task StopAsync()
        {
            _shutdownCts?.Cancel();
            await IgnoreFailureAsync(_supervisor, "supervising the bridge").ConfigureAwait(false);
            await DisposeBridgeAsync().ConfigureAwait(false);
            _events?.Writer.TryComplete();
            await IgnoreFailureAsync(_worker, "handling the eufy-sdk events").ConfigureAwait(false);
            _shutdownCts?.Dispose();
            _shutdownCts = null;
            _events = null;
            _supervisor = Task.CompletedTask;
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

        /// <summary>The install id eufy-sdk reports: its own, so the C# Eufy Security device can be logged in too.</summary>
        internal static string InstallId(string email) => Hash("riot2-eufy-sdk:" + email);

        private static string Hash(string text) =>
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.Trim().ToLowerInvariant())))[..16];

        private async Task LaunchAndLoginAsync(CancellationToken cancellationToken)
        {
            var options = _options!;
            SetStatus(new EufyServiceStatus(EufyConnectionState.Connecting));
            await DisposeBridgeAsync().ConfigureAwait(false);

            var connection = await _launcher.StartAsync(options.DataDirectory, cancellationToken).ConfigureAwait(false);
            var bridge = new EufyBridgeClient(connection, _logger);
            bridge.EventReceived += OnBridgeEvent;
            _bridge = bridge;
            _bridgeStarted = _time.GetUtcNow();

            var versions = await InvokeAsync("ping", null, QuickTimeout, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("eufy-sdk {Sdk} runs on Node.js {Node}.", Text(versions, "sdk"), Text(versions, "node"));

            var account = Hash(options.Email);
            var data = Path.GetFullPath(options.DataDirectory);
            await InvokeAsync("start", new
            {
                email = options.Email,
                password = options.Password,
                countryCode = options.CountryCode,
                openudid = InstallId(options.Email),
                sessionFile = Path.Combine(data, $"eufy-sdk-session-{account}.json"),
                pushFile = Path.Combine(data, $"eufy-sdk-push-{account}.json"),
                ffmpegPath = options.FfmpegPath
            }, QuickTimeout, cancellationToken).ConfigureAwait(false);

            var login = await InvokeAsync("login", null, LoginTimeout, cancellationToken).ConfigureAwait(false);
            await HandleLoginResultAsync(login, cancellationToken).ConfigureAwait(false);
        }

        private async Task HandleLoginResultAsync(JsonElement result, CancellationToken cancellationToken)
        {
            switch (Text(result, "status"))
            {
                case "ok":
                    await ConnectAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case "captcha":
                    _logger.LogWarning("The eufy cloud asks for a captcha; answer it with the device's verify command.");
                    SetStatus(new EufyServiceStatus(EufyConnectionState.CaptchaRequired,
                        result.TryGetProperty("retry", out var retry) && retry.ValueKind == JsonValueKind.True
                            ? "The captcha answer was wrong; answer the new captcha."
                            : "Answer the captcha with the verify command.",
                        CaptchaImage: Text(result, "image")));
                    break;
                case "2fa":
                    var method = Text(result, "method");
                    _logger.LogWarning("The eufy cloud sent a verification code ({Method}); answer it with the device's verify command.", method);
                    SetStatus(new EufyServiceStatus(EufyConnectionState.TwoFactorRequired, "Enter the verification code with the verify command.", TwoFactorMethod: method));
                    break;
                default:
                    throw new EufySdkException($"eufy-sdk answered the login with an unknown status '{Text(result, "status")}'.");
            }
        }

        private async Task ConnectAsync(CancellationToken cancellationToken)
        {
            var result = await InvokeAsync("connect", null, ConnectTimeout, cancellationToken).ConfigureAwait(false);
            var devices = ApplyDevices(result);
            _logger.LogInformation("Connected to the eufy cloud through eufy-sdk: {Stations} station(s), {Devices} device(s), realtime {Realtime}.",
                devices.Count(d => d.IsStation), devices.Count(d => !d.IsStation), Text(result, "realtime") ?? "unknown");
            SetStatus(new EufyServiceStatus(EufyConnectionState.Connected));
        }

        // Restarts the bridge when it exits on its own, until the service stops.
        private async Task SuperviseAsync(CancellationToken shutdown)
        {
            var delay = _restartDelay;
            while (!shutdown.IsCancellationRequested)
            {
                var bridge = _bridge;
                if (bridge == null)
                    return;

                int exitCode;
                try
                {
                    exitCode = await bridge.Exited.WaitAsync(shutdown).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (_time.GetUtcNow() - _bridgeStarted >= StableRun)
                    delay = _restartDelay;
                _logger.LogWarning("eufy-sdk stopped (exit code {ExitCode}); it restarts in {Delay}.", exitCode, delay);
                SetStatus(new EufyServiceStatus(EufyConnectionState.Connecting, $"eufy-sdk stopped (exit code {exitCode}); restarting."));

                while (!shutdown.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(delay, _time, shutdown).ConfigureAwait(false);
                        delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, RestartDelayMax.Ticks));
                        await _gate.WaitAsync(shutdown).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    try
                    {
                        await LaunchAndLoginAsync(shutdown).ConfigureAwait(false);
                        break;
                    }
                    catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception error)
                    {
                        _logger.LogError(error, "Restarting eufy-sdk failed; it is tried again in {Delay}.", delay);
                        SetStatus(new EufyServiceStatus(EufyConnectionState.Error, error.Message));
                        await DisposeBridgeAsync().ConfigureAwait(false);
                    }
                    finally
                    {
                        _gate.Release();
                    }
                }
            }
        }

        private async Task<JsonElement> InvokeAsync(string method, object? parameters, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var bridge = _bridge ?? throw new InvalidOperationException("eufy-sdk is not running.");
            return await bridge.InvokeAsync(method, parameters, timeout, cancellationToken).ConfigureAwait(false);
        }

        private void EnsureConnected()
        {
            if (_bridge == null || _status.State != EufyConnectionState.Connected)
                throw new InvalidOperationException($"The eufy account is not connected ({_status.State}).");
        }

        private List<EufyDeviceRecord> ApplyDevices(JsonElement result)
        {
            var devices = new List<EufyDeviceRecord>();
            if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("devices", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in list.EnumerateArray())
                {
                    if (ToRecord(item) is { } record)
                        devices.Add(record);
                }
            }

            _devices = devices;
            foreach (var station in devices.Where(d => d.IsStation))
            {
                if (station.Params.TryGetValue(GuardModeParam, out var raw) &&
                    int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mode))
                    SetGuardMode(station.Serial, mode);
            }

            return devices;
        }

        internal static EufyDeviceRecord? ToRecord(JsonElement item)
        {
            if (item.ValueKind != JsonValueKind.Object || Text(item, "sn") is not { Length: > 0 } serial)
                return null;

            var parameters = new Dictionary<int, string>();
            if (item.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in p.EnumerateObject())
                {
                    if (int.TryParse(property.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                        parameters[id] = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString()! : property.Value.GetRawText();
                }
            }

            return new EufyDeviceRecord
            {
                Serial = serial,
                Name = Text(item, "name") is { Length: > 0 } name ? name : serial,
                Model = Text(item, "model"),
                StationSerial = Text(item, "stationSn") is { Length: > 0 } station ? station : serial,
                Channel = Int(item, "channel"),
                P2pDid = Text(item, "p2pDid"),
                DeviceType = Int(item, "deviceType"),
                Params = parameters,
                Raw = item.Clone()
            };
        }

        internal static (EufySecurityEvent Event, byte[]? Picture)? ToSecurityEvent(JsonElement data)
        {
            if (data.ValueKind != JsonValueKind.Object || Text(data, "name") is not { } name || !EventKinds.TryGetValue(name, out var kind))
                return null;

            byte[]? picture = null;
            if (Text(data, "picture") is { Length: > 0 } base64)
            {
                try
                {
                    picture = Convert.FromBase64String(base64);
                }
                catch (FormatException)
                {
                }
            }

            var phase = Text(data, "phase") switch
            {
                "triggered" => EufyAlarmPhase.Triggered,
                "delayed" => EufyAlarmPhase.Delayed,
                "stopped" => EufyAlarmPhase.Stopped,
                _ => (EufyAlarmPhase?)null
            };

            return (new EufySecurityEvent
            {
                Kind = kind,
                DeviceSerial = Text(data, "deviceSn"),
                StationSerial = Text(data, "stationSn"),
                PersonName = Text(data, "personName"),
                EventType = Int(data, "eventType"),
                AlarmType = Int(data, "alarmType"),
                AlarmPhase = kind == EufyEventKind.Alarm ? phase ?? EufyAlarmPhase.Triggered : phase
            }, picture);
        }

        private void OnBridgeEvent(string name, JsonElement data)
        {
            switch (name)
            {
                case "security":
                    if (ToSecurityEvent(data) is { } securityEvent)
                        EnqueueEvent(securityEvent.Event, securityEvent.Picture);
                    break;
                case "guardMode":
                    if (Text(data, "stationSn") is { Length: > 0 } station && Int(data, "mode") is int mode)
                        SetGuardMode(station, mode);
                    break;
                case "sessionExpired":
                    _logger.LogError("eufy-sdk could not renew the eufy session: {Message}", Text(data, "message"));
                    SetStatus(new EufyServiceStatus(EufyConnectionState.Error,
                        "The eufy session ended and could not be renewed; restart the device to log in again. " + Text(data, "message")));
                    break;
                case "push":
                    if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("connected", out var connected) && connected.ValueKind == JsonValueKind.True)
                        _logger.LogInformation("The eufy-sdk push channel is connected.");
                    else
                        _logger.LogWarning("The eufy-sdk push channel disconnected; eufy-sdk reconnects it.");
                    break;
                case "fatal":
                    _logger.LogError("eufy-sdk cannot run: {Message}", Text(data, "message"));
                    SetStatus(new EufyServiceStatus(EufyConnectionState.Error, Text(data, "message")));
                    break;
                default:
                    _logger.LogDebug("eufy-sdk event {Event}.", name);
                    break;
            }
        }

        internal void EnqueueEvent(EufySecurityEvent securityEvent, byte[]? picture)
        {
            var events = _events;
            if (events == null || !events.Writer.TryWrite((securityEvent, picture)))
                _logger.LogDebug("eufy-sdk event received while the service is stopped; ignored.");
        }

        private async Task ProcessEventsAsync(ChannelReader<(EufySecurityEvent Event, byte[]? Picture)> reader, CancellationToken cancellationToken)
        {
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (reader.TryRead(out var item))
                {
                    try
                    {
                        if (item.Event.Kind == EufyEventKind.ArmingModeChanged)
                        {
                            // eufy-sdk announces the new mode itself; the device list confirms it.
                            await Task.Delay(_armingRefreshDelay, _time, cancellationToken).ConfigureAwait(false);
                            await RefreshAsync(cancellationToken).ConfigureAwait(false);
                        }
                        else
                        {
                            Raise(() => SecurityEvent?.Invoke(item.Event, item.Picture), "security");
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        // Stopping: the remaining events are drained without handling.
                    }
                    catch (Exception error)
                    {
                        _logger.LogError(error, "Error handling a eufy-sdk {Kind} event; the next events are still handled.", item.Event.Kind);
                    }
                }
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

        private void SetStatus(EufyServiceStatus status)
        {
            _status = status;
            Raise(() => StatusChanged?.Invoke(status), "status");
        }

        private async Task DisposeBridgeAsync()
        {
            var bridge = _bridge;
            _bridge = null;
            if (bridge == null)
                return;
            bridge.EventReceived -= OnBridgeEvent;
            await IgnoreFailureAsync(bridge.DisposeAsync().AsTask(), "stopping the bridge").ConfigureAwait(false);
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
                _logger.LogError(error, "A eufy-sdk {Kind} event handler failed.", kind);
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
                _logger.LogDebug(error, "eufy-sdk: {What} failed while stopping.", what);
            }
        }

        private static string? Text(JsonElement element, string property) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        private static int? Int(JsonElement element, string property) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
                ? number
                : null;
    }
}
