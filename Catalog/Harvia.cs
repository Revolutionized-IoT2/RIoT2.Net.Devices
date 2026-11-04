using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using RIoT2.Core;
using RIoT2.Core.Abstracts;
using RIoT2.Core.Interfaces;
using RIoT2.Core.Models;
using RIoT2.Core.Models.Matter;
using RIoT2.Core.Plugins;
using RIoT2.Net.Devices.Models;
using RIoT2.Net.Devices.Services;
using RIoT2.Net.Devices.Services.Harvia;
using ValueType = RIoT2.Core.ValueType;

namespace RIoT2.Net.Devices.Catalog
{
    /// <summary>The <see cref="Harvia"/> device's parameters: the MyHarvia account, and the heater for the energy estimate.</summary>
    public sealed class HarviaOptions
    {
        [DeviceParameter("email", Required = true, Description = "E-mail address of the MyHarvia account")]
        public string Email { get; set; } = "";

        [DeviceParameter("password", Required = true, Secret = true, Description = "Password of the MyHarvia account")]
        public string Password { get; set; } = "";

        [DeviceParameter("heaterPower", Default = "0", Description = "Rated power of the heater in kW, e.g. 9; turns on the energy estimate (0 = off)")]
        public double HeaterPower { get; set; }

        [DeviceParameter("readyTemperature", Default = "0",
            Description = "Cabin temperature (°C) at which the sauna counts as ready; 0 = when the target temperature is reached")]
        public double ReadyTemperature { get; set; }
    }

    /// <summary>
    /// Harvia saunas with a Xenio WiFi panel, through the MyHarvia cloud the MyHarvia app uses. MyHarvia pushes the
    /// changes over a WebSocket; a poll every 5 minutes reads everything again. Reports each sauna's state, temperature,
    /// humidity, target, remaining time, door, light, sessions, readiness, the panel's schedule and an energy estimate;
    /// commands switch it on or off, set the target temperature and switch the light. Each sauna is a Matter thermostat
    /// with temperature, humidity and door sensors and a light.
    /// </summary>
    [RequiresHardware("MyHarvia cloud")]
    public class Harvia : AsyncDeviceBase<HarviaOptions>, IDeviceWithConfiguration, IAsyncCommandDevice, IMatterDevice
    {
        // Report and command keys; an address is "<device id>|<key>".
        internal const string Power = "power";
        internal const string StateKey = "state";
        internal const string Mode = "mode";
        internal const string Temperature = "temperature";
        internal const string Humidity = "humidity";
        internal const string TargetTemperature = "targetTemperature";
        internal const string RemainingTime = "remainingTime";
        internal const string Heating = "heating";
        internal const string Door = "door";
        internal const string Light = "light";
        internal const string Connected = "connected";
        internal const string SessionEvent = "sessionEvent";
        internal const string LastSession = "lastSession";
        internal const string SessionsToday = "sessionsToday";
        internal const string BathingMinutesToday = "bathingMinutesToday";
        internal const string HeaterPowerNow = "heaterPowerNow";
        internal const string EnergyLastSession = "energyLastSession";
        internal const string EnergyToday = "energyToday";
        internal const string Ready = "ready";
        internal const string TimeToReady = "timeToReady";
        internal const string ReadyAt = "readyAt";
        internal const string ScheduleEnabled = "scheduleEnabled";
        internal const string ScheduleReadyAt = "scheduleReadyAt";
        internal const string ScheduleStartAt = "scheduleStartAt";
        internal const string ScheduleDuration = "scheduleDuration";
        internal const string ScheduleTargetTemperature = "scheduleTargetTemperature";
        internal const string RefreshAddress = "refresh";

        /// <summary>The target temperatures the Xenio panel takes.</summary>
        internal const int MinTemperature = 40;
        internal const int MaxTemperature = 110;

        /// <summary>The Matter thermostat's SystemMode values: off, and heat (the sauna on).</summary>
        internal const int MatterOff = 0;
        internal const int MatterHeat = 4;

        /// <summary>The shortest time between two polls.</summary>
        internal static readonly TimeSpan MinimumPollInterval = TimeSpan.FromMinutes(1);

        /// <summary>The panel reports about every 14 minutes while idle; silence longer than this means it is offline.</summary>
        internal static readonly TimeSpan ConnectedWindow = TimeSpan.FromMinutes(30);

        /// <summary>Telemetry heating while the state says off this long means a lost push: the state is read again.</summary>
        internal static readonly TimeSpan RecheckDelay = TimeSpan.FromMinutes(1);

        internal static readonly TimeSpan RecheckInterval = TimeSpan.FromMinutes(5);

        private readonly object _lock = new();
        // Applies one sauna update at a time: a push and a poll don't interleave sessions and reports.
        private readonly object _applyLock = new();
        private readonly Dictionary<string, HarviaSauna> _saunas = [];
        private readonly Dictionary<string, HarviaSessionState> _sessions = [];
        private readonly Dictionary<string, HarviaTrend> _trends = [];
        private readonly Dictionary<string, DateTimeOffset> _rechecks = [];
        private HarviaClient? _client;
        private CancellationTokenSource? _listenerStop;
        private Task? _listener;
        private List<HarviaPushConnection> _connections = [];
        private DateTimeOffset? _lastPoll;

        public Harvia(ILogger logger) : base(logger) { }

        /// <summary>The folder of the token and session files; tests replace it.</summary>
        internal string DataDirectory { get; set; } = "Data";

        /// <summary>The clock; tests replace it.</summary>
        internal TimeProvider Time { get; set; } = TimeProvider.System;

        /// <summary>Whether to listen to MyHarvia's pushes; tests that only poll switch it off.</summary>
        internal bool ListenToPushes { get; set; } = true;

        internal HarviaPushSettings PushSettings { get; set; } = HarviaPushSettings.Default;

        /// <summary>The running push listener, for tests.</summary>
        internal Task? Listener => _listener;

        /// <summary>The push connections, for tests.</summary>
        internal IReadOnlyList<HarviaPushConnection> PushConnections
        {
            get { lock (_lock) return _connections.ToList(); }
        }

        // The HTTP call; the tests replace it.
        internal virtual Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            DirectHttp.SendAsync(request, "MyHarvia", cancellationToken);

        // The WebSocket; the tests replace it.
        internal virtual Task<IHarviaSocket> ConnectAsync(Uri url, CancellationToken cancellationToken) =>
            HarviaWebSocket.ConnectAsync(url, cancellationToken);

        private double HeaterPowerKw => Options.HeaterPower > 0 ? Options.HeaterPower : 0;

        private double ReadyTemperatureOption => Options.ReadyTemperature > 0 ? Options.ReadyTemperature : 0;

        public override void ConfigureDevice()
        {
            _client = null;
            _lastPoll = null;
            lock (_lock)
            {
                _saunas.Clear();
                _sessions.Clear();
                _trends.Clear();
                _rechecks.Clear();
                _connections = [];
            }
        }

        protected override async Task StartDeviceAsync(CancellationToken cancellationToken)
        {
            var email = Options.Email.Trim();
            _client = new HarviaClient(email, Options.Password, SendAsync, TokenFile(email), Logger, Time);

            // The first poll's errors, a refused login too, put the device into the error state with the reason. A
            // configured device outlives a MyHarvia outage at start: the pushes and the next refresh try again.
            try
            {
                await PollAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (HarviaUnavailableException error) when (ReportTemplates?.Any() == true || CommandTemplates?.Any() == true)
            {
                Logger.LogWarning("MyHarvia is unavailable; the pushes and the next refresh try again: {Reason}", error.Message);
            }

            if (ListenToPushes)
            {
                _listenerStop = new CancellationTokenSource();
                var client = _client;
                _listener = Task.Run(() => ListenAsync(client, _listenerStop.Token), CancellationToken.None);
            }
        }

        protected override async Task StopDeviceAsync(CancellationToken cancellationToken)
        {
            var stop = _listenerStop;
            var listener = _listener;
            _listenerStop = null;
            _listener = null;
            _client = null;
            if (stop == null)
                return;
            await stop.CancelAsync().ConfigureAwait(false);
            if (listener != null)
            {
                try
                {
                    await listener.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    Logger.LogWarning("MyHarvia's push listener didn't stop in 10 seconds.");
                }
            }
            stop.Dispose();
        }

        // Called by the scheduler; the refresh is defined on the device, so the report is null.
        protected override Task RefreshAsync(ReportTemplate? report, CancellationToken cancellationToken) =>
            report != null ? Task.CompletedTask : RefreshSaunasAsync(cancellationToken);

        private async Task RefreshSaunasAsync(CancellationToken cancellationToken)
        {
            if (_lastPoll is DateTimeOffset last && Time.GetUtcNow() - last < MinimumPollInterval)
            {
                Logger.LogDebug("MyHarvia was polled less than a minute ago; the refresh is skipped.");
                return;
            }

            try
            {
                await PollAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (HarviaUnavailableException error)
            {
                Logger.LogWarning("Could not refresh MyHarvia: {Reason}", error.Message);
            }
        }

        private async Task PollAsync(CancellationToken cancellationToken)
        {
            var client = _client ?? throw new DeviceNotReadyException("The Harvia device isn't started.");
            var ids = await client.GetDeviceIdsAsync(cancellationToken).ConfigureAwait(false);
            var updates = new List<(HarviaUpdate State, HarviaUpdate? Telemetry)>();
            foreach (var id in ids)
            {
                try
                {
                    var state = await client.GetStateAsync(id, cancellationToken).ConfigureAwait(false);
                    HarviaUpdate? telemetry = null;
                    try
                    {
                        telemetry = await client.GetLatestDataAsync(id, cancellationToken).ConfigureAwait(false);
                    }
                    catch (HarviaRefusedException error)
                    {
                        Logger.LogDebug("MyHarvia refused the measurements of {Sauna}: {Reason}", id, error.Message);
                    }
                    updates.Add((state, telemetry));
                }
                catch (HarviaRefusedException error)
                {
                    Logger.LogWarning("MyHarvia refused the state of {Sauna}; it is left out: {Reason}", id, error.Message);
                }
            }

            lock (_lock)
            {
                foreach (var gone in _saunas.Keys.Where(x => !ids.Contains(x)).ToList())
                    _saunas.Remove(gone);
            }
            _lastPoll = Time.GetUtcNow();

            if (ids.Count == 0)
                Logger.LogWarning("The MyHarvia account has no saunas.");
            foreach (var (state, telemetry) in updates)
                Apply(state.DeviceId, state.State, state.At, telemetry?.Telemetry, telemetry?.At, fromPush: false);
        }

        private async Task ListenAsync(HarviaClient client, CancellationToken cancellationToken)
        {
            var delay = PushSettings.ReconnectDelay;
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var endpoints = await client.GetEndpointsAsync(cancellationToken).ConfigureAwait(false);
                    var user = await client.GetUserAsync(cancellationToken).ConfigureAwait(false);
                    if (user.Receivers.Count == 0)
                    {
                        Logger.LogWarning("MyHarvia's user has no e-mail or organization to receive pushes; the sauna is polled only.");
                        return;
                    }
                    var connections = new List<HarviaPushConnection>
                    {
                        Connection("device", endpoints.Device, HarviaClient.StateSubscription, user.Receivers, client),
                        Connection("data", endpoints.Data, HarviaClient.DataSubscription, user.Receivers, client)
                    };
                    lock (_lock)
                        _connections = connections;
                    await Task.WhenAll(connections.Select(c => c.RunAsync(cancellationToken))).ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (HarviaAuthenticationException error)
                {
                    Logger.LogError("MyHarvia's pushes need a login: {Reason}", error.Message);
                    delay = PushSettings.MaxReconnectDelay;
                }
                catch (Exception error)
                {
                    Logger.LogWarning("MyHarvia's pushes can't start; trying again in {Delay}: {Reason}", delay, error.Message);
                }

                try
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, PushSettings.MaxReconnectDelay.Ticks));
            }
        }

        private HarviaPushConnection Connection(string service, string endpoint, string query, IReadOnlyList<string> receivers, HarviaClient client) =>
            new(service, endpoint, query, receivers, client.GetIdTokenAsync, ConnectAsync, HandlePush, PushSettings, Logger);

        /// <summary>A pushed state or telemetry. A sauna that no poll found yet is left to the next poll.</summary>
        internal void HandlePush(HarviaUpdate update)
        {
            lock (_lock)
            {
                if (!_saunas.ContainsKey(update.DeviceId))
                    return;
            }
            Apply(update.DeviceId, update.State, update.At, update.Telemetry, update.At, fromPush: true);
        }

        /// <summary>
        /// Takes a sauna's state and telemetry, from a poll or a push: follows its session, readiness and energy, keeps
        /// the day's totals and sends the reports.
        /// </summary>
        private void Apply(string id, JsonObject? state, DateTimeOffset? stateAt, JsonObject? telemetry, DateTimeOffset? telemetryAt, bool fromPush)
        {
            lock (_applyLock)
            {
                var now = Time.GetUtcNow();
                HarviaSauna sauna;
                HarviaTrend trend;
                lock (_lock)
                {
                    if (!_saunas.TryGetValue(id, out sauna!))
                        _saunas[id] = sauna = new HarviaSauna(id);
                    if (state != null)
                        sauna.MergeState(state, stateAt ?? now);
                    if (telemetry != null)
                        sauna.MergeTelemetry(telemetry, telemetryAt ?? now);
                    if (!_trends.TryGetValue(id, out trend!))
                        _trends[id] = trend = new HarviaTrend();
                }

                if (telemetry != null && sauna.Temperature is double temperature)
                    trend.Add(now, temperature);

                var day = TimeZoneInfo.ConvertTime(now, Time.LocalTimeZone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                var session = SessionState(id);
                var update = HarviaSessions.Update(session, sauna, now, day, HeaterPowerKw, ReadyTemperatureOption);
                if (update.Changed)
                    SaveSessionState(id, session);
                if (sauna.Active == false)
                    trend.Clear();

                SendSaunaReports(sauna, session, trend, now);
                foreach (var item in update.Events)
                    SendAlways(id, SessionEvent, new ValueModel(item), now);
                if (update.Ended is HarviaSession ended)
                    SendSessionReports(sauna, ended);

                if (fromPush && state == null)
                    CheckForLostPush(sauna, now);
            }
        }

        // Telemetry says the heater is on while the state says off: a state push was probably lost (measured on a
        // Fenix started at the panel). Reads the state again, at most every few minutes.
        private void CheckForLostPush(HarviaSauna sauna, DateTimeOffset now)
        {
            if (sauna.HeatOn != true || sauna.Active != false)
            {
                sauna.HeatingWhileOffSince = null;
                return;
            }
            sauna.HeatingWhileOffSince ??= now;
            if (now - sauna.HeatingWhileOffSince < RecheckDelay)
                return;
            lock (_lock)
            {
                if (_rechecks.TryGetValue(sauna.Id, out var last) && now - last < RecheckInterval)
                    return;
                _rechecks[sauna.Id] = now;
            }
            var client = _client;
            if (client == null)
                return;
            Logger.LogDebug("{Sauna} heats while its state says off; reading the state again.", sauna.Name);
            _ = Task.Run(async () =>
            {
                try
                {
                    var update = await client.GetStateAsync(sauna.Id, CancellationToken.None).ConfigureAwait(false);
                    Apply(update.DeviceId, update.State, update.At, null, null, fromPush: false);
                }
                catch (Exception error) when (error is HarviaUnavailableException or HarviaRefusedException or HarviaAuthenticationException)
                {
                    Logger.LogDebug("Could not read the state of {Sauna} again: {Reason}", sauna.Name, error.Message);
                }
            }, CancellationToken.None);
        }

        public async Task ExecuteCommandAsync(string commandId, string value, CancellationToken cancellationToken)
        {
            var command = CommandTemplates?.FirstOrDefault(x => x.Id == commandId) ?? throw new UnknownCommandException(commandId);
            if (string.Equals(command.Address, RefreshAddress, StringComparison.OrdinalIgnoreCase))
            {
                await RefreshSaunasAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            var (id, key) = SplitAddress(command.Address);
            if (id == null)
                throw new UnknownCommandException(commandId);

            var client = _client;
            var sauna = FindSauna(id);
            if (sauna == null && client != null && _lastPoll == null)
            {
                // MyHarvia was unavailable at start: read the saunas now.
                await PollAsync(cancellationToken).ConfigureAwait(false);
                sauna = FindSauna(id);
            }
            if (client == null || sauna == null)
                throw new DeviceNotReadyException($"Harvia sauna {id} isn't loaded yet.");

            JsonObject change;
            lock (_applyLock)
                change = ParseCommand(key, value, sauna) ?? throw new UnknownCommandException(commandId);
            try
            {
                await client.RequestStateChangeAsync(id, change, cancellationToken).ConfigureAwait(false);
            }
            catch (HarviaRefusedException error)
            {
                throw new InvalidOperationException($"MyHarvia refused the command: {error.Message}", error);
            }
            Logger.LogInformation("Asked {Sauna} for {Change}.", sauna.Name, change.ToJsonString());
        }

        /// <summary>
        /// The state change a command asks for, or null for an unknown key. A value that doesn't fit throws
        /// <see cref="ArgumentException"/>; switching on with the door open throws <see cref="InvalidOperationException"/>.
        /// </summary>
        internal static JsonObject? ParseCommand(string key, string value, HarviaSauna sauna)
        {
            switch (key)
            {
                case Power:
                    return Switch(sauna, CommandBool(value));
                case Mode:
                    var mode = CommandNumber(value);
                    return mode switch
                    {
                        MatterOff => Switch(sauna, false),
                        MatterHeat => Switch(sauna, true),
                        _ => throw new ArgumentOutOfRangeException(nameof(value), value, $"The mode is {MatterOff} (off) or {MatterHeat} (heat).")
                    };
                case TargetTemperature:
                    var temperature = (int)Math.Round(CommandNumber(value), MidpointRounding.AwayFromZero);
                    if (temperature is < MinTemperature or > MaxTemperature)
                        throw new ArgumentOutOfRangeException(nameof(value), value, $"The target temperature must be {MinTemperature}-{MaxTemperature} °C.");
                    return new JsonObject { ["targetTemp"] = temperature };
                case Light:
                    return new JsonObject { ["light"] = CommandBool(value) ? 1 : 0 };
                default:
                    return null;
            }
        }

        // The panel refuses a remote start with the door or safety circuit open anyway; saying so beats a silent no-op.
        private static JsonObject Switch(HarviaSauna sauna, bool on)
        {
            if (on && sauna.DoorOpen == true)
                throw new InvalidOperationException($"Harvia refuses a remote start of {sauna.Name} while the door or safety circuit is open. Close the door and try again.");
            return new JsonObject { ["active"] = on ? 1 : 0 };
        }

        // A command value arrives as JSON: true, 80, "on". A bare word is taken as text.
        private static JsonElement CommandJson(string value)
        {
            try
            {
                using var document = JsonDocument.Parse(value);
                return document.RootElement.Clone();
            }
            catch (JsonException)
            {
                return JsonSerializer.SerializeToElement(value);
            }
        }

        private static double CommandNumber(string value)
        {
            var json = CommandJson(value);
            if (json.ValueKind == JsonValueKind.Number)
                return json.GetDouble();
            if (json.ValueKind == JsonValueKind.String &&
                double.TryParse(json.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                return number;
            throw new ArgumentException($"'{value}' is not a number.", nameof(value));
        }

        private static bool CommandBool(string value)
        {
            var json = CommandJson(value);
            switch (json.ValueKind)
            {
                case JsonValueKind.True:
                    return true;
                case JsonValueKind.False:
                    return false;
                case JsonValueKind.Number:
                    return json.GetDouble() != 0;
                case JsonValueKind.String:
                    var text = json.GetString()?.Trim().ToLowerInvariant();
                    if (text is "true" or "on" or "1")
                        return true;
                    if (text is "false" or "off" or "0")
                        return false;
                    break;
            }
            throw new ArgumentException($"'{value}' is not true or false.", nameof(value));
        }

        internal static string Address(string id, string key) => $"{id}|{key}";

        private static (string? Id, string Key) SplitAddress(string? address)
        {
            var separator = address?.LastIndexOf('|') ?? -1;
            return separator <= 0 ? (null, "") : (address![..separator], address[(separator + 1)..]);
        }

        private HarviaSauna? FindSauna(string id)
        {
            lock (_lock)
                return _saunas.GetValueOrDefault(id);
        }

        private ReportTemplate? FindTemplate(string id, string key)
        {
            var address = Address(id, key);
            return ReportTemplates?.FirstOrDefault(x => string.Equals(x.Address, address, StringComparison.OrdinalIgnoreCase));
        }

        private IEnumerable<(string Key, object? Value)> Values(HarviaSauna sauna, HarviaSessionState session, HarviaTrend trend, DateTimeOffset now)
        {
            yield return (Power, sauna.Active);
            yield return (StateKey, sauna.StateName);
            yield return (Mode, sauna.Active is bool active ? active ? MatterHeat : MatterOff : null);
            yield return (Temperature, sauna.Temperature);
            yield return (Humidity, sauna.Humidity);
            yield return (TargetTemperature, sauna.TargetTemperature);
            yield return (RemainingTime, sauna.RemainingMinutes);
            yield return (Heating, sauna.Heating);
            yield return (Door, sauna.DoorOpen);
            yield return (Light, sauna.Light);
            yield return (Connected, sauna.LastSeen is DateTimeOffset seen && now - seen <= ConnectedWindow);
            yield return (SessionsToday, session.SessionsToday);
            yield return (BathingMinutesToday, session.BathingMinutesToday);
            if (HeaterPowerKw > 0)
            {
                yield return (HeaterPowerNow, sauna.Heating == true ? HeaterPowerKw : 0d);
                yield return (EnergyToday, Math.Round(session.EnergyToday, 3));
            }

            var open = session.Open;
            yield return (Ready, open?.ReadyAt != null);
            if (open == null)
            {
                yield return (TimeToReady, 0);
                yield return (ReadyAt, "");
            }
            else if (open.ReadyAt is DateTimeOffset ready)
            {
                yield return (TimeToReady, 0);
                yield return (ReadyAt, Local(ready));
            }
            else if ((ReadyTemperatureOption > 0 ? ReadyTemperatureOption : (double?)sauna.TargetTemperature) is double target &&
                sauna.Temperature is double temperature && trend.MinutesTo(target, temperature) is int minutes)
            {
                yield return (TimeToReady, minutes);
                yield return (ReadyAt, Local(now.AddMinutes(minutes)));
            }

            var schedule = sauna.Schedule;
            var armed = schedule != null && schedule.Enabled && schedule.ReadyAt > now;
            yield return (ScheduleEnabled, armed);
            yield return (ScheduleReadyAt, armed ? Local(schedule!.ReadyAt) : "");
            yield return (ScheduleStartAt, armed ? Local(schedule!.ReadyAt.AddMinutes(-(sauna.HeatUpMinutes ?? 0))) : "");
            yield return (ScheduleDuration, armed ? schedule!.DurationMinutes : 0);
            yield return (ScheduleTargetTemperature, armed ? schedule!.TargetTemperature : 0);
        }

        private void SendSaunaReports(HarviaSauna sauna, HarviaSessionState session, HarviaTrend trend, DateTimeOffset now)
        {
            var timeStamp = now.ToUnixTimeSeconds();
            foreach (var (key, value) in Values(sauna, session, trend, now))
            {
                if (value == null || FindTemplate(sauna.Id, key) is not ReportTemplate template)
                    continue;
                SendReportIfValueChanged(this, new Report
                {
                    Id = template.Id,
                    TimeStamp = timeStamp,
                    Value = new ValueModel(value),
                    Filter = ""
                });
            }
        }

        // Each session once, time-stamped at its end.
        private void SendSessionReports(HarviaSauna sauna, HarviaSession session)
        {
            var lastSession = new Dictionary<string, object?>
            {
                ["startedAt"] = Local(session.StartedAt),
                ["readyAt"] = session.ReadyAt is DateTimeOffset ready ? Local(ready) : "",
                ["endedAt"] = Local(session.EndedAt),
                ["durationMinutes"] = session.DurationMinutes,
                ["heatUpMinutes"] = session.HeatUpMinutes,
                ["maxTemperature"] = session.MaxTemperature,
                ["targetTemperature"] = session.TargetTemperature,
                ["energy"] = session.Energy
            };
            SendAlways(sauna.Id, LastSession, new ValueModel(lastSession), session.EndedAt);
            if (session.Energy is double energy)
                SendAlways(sauna.Id, EnergyLastSession, new ValueModel(energy), session.EndedAt);
            Logger.LogInformation("{Sauna} session ended after {Minutes} min (heat-up {HeatUp} min, max {Max} °C, estimated {Energy} kWh).", sauna.Name,
                session.DurationMinutes, session.HeatUpMinutes?.ToString(CultureInfo.InvariantCulture) ?? "-",
                session.MaxTemperature?.ToString(CultureInfo.InvariantCulture) ?? "-", session.Energy?.ToString(CultureInfo.InvariantCulture) ?? "-");
        }

        private void SendAlways(string id, string key, ValueModel value, DateTimeOffset at)
        {
            if (FindTemplate(id, key) is not ReportTemplate template)
                return;
            SendReport(this, new Report { Id = template.Id, TimeStamp = at.ToUnixTimeSeconds(), Value = value, Filter = "" });
        }

        private string Local(DateTimeOffset time)
        {
            var local = TimeZoneInfo.ConvertTime(time, Time.LocalTimeZone);
            return local.AddTicks(-(local.Ticks % TimeSpan.TicksPerSecond)).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
        }

        internal string TokenFile(string email) => Path.Combine(DataDirectory, $"harvia-auth-{Hash(email.ToLowerInvariant())}.json");

        internal string SessionFile(string id) => Path.Combine(DataDirectory, $"harvia-{Hash(id)}.json");

        private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

        private HarviaSessionState SessionState(string id)
        {
            lock (_lock)
            {
                if (_sessions.TryGetValue(id, out var cached))
                    return cached;
            }
            var file = SessionFile(id);
            HarviaSessionState? state = null;
            try
            {
                state = File.Exists(file) ? JsonSerializer.Deserialize<HarviaSessionState>(File.ReadAllText(file)) : null;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                Logger.LogWarning(error, "Could not read the Harvia session file {File}; today's totals count from now.", file);
            }
            state ??= new HarviaSessionState();
            lock (_lock)
                _sessions[id] = state;
            return state;
        }

        private void SaveSessionState(string id, HarviaSessionState state)
        {
            var file = SessionFile(id);
            try
            {
                var directory = Path.GetDirectoryName(file);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);
                var temporary = file + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(state));
                File.Move(temporary, file, overwrite: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Logger.LogWarning(error, "Could not save the Harvia session file {File}.", file);
            }
        }

        public DeviceConfiguration GetConfigurationTemplate()
        {
            var configuration = new DeviceConfiguration
            {
                Id = Guid.NewGuid().ToString(),
                Name = "Harvia",
                ClassFullName = GetType().FullName!,
                // Every 5 minutes: a safety poll; the pushes bring the changes.
                RefreshSchedule = "0 0/5 * ? * *",
                DeviceParameters = new Dictionary<string, string>
                {
                    ["email"] = "user@example.com",
                    ["password"] = "",
                    ["heaterPower"] = "0",
                    ["readyTemperature"] = "0"
                },
                ReportTemplates = [],
                CommandTemplates = []
            };

            if (State != DeviceState.Running)
                return configuration;

            List<HarviaSauna> saunas;
            lock (_lock)
                saunas = _saunas.Values.OrderBy(x => x.Name, StringComparer.Ordinal).ThenBy(x => x.Id, StringComparer.Ordinal).ToList();

            var reports = new List<ReportTemplate>();
            var commands = new List<CommandTemplate>();
            foreach (var sauna in saunas)
            {
                reports.AddRange(SaunaReports(sauna, HeaterPowerKw > 0));
                commands.AddRange(SaunaCommands(sauna));
            }
            commands.Add(new CommandTemplate
            {
                Id = Guid.NewGuid().ToString(),
                Name = "Refresh Harvia",
                Address = RefreshAddress,
                Type = ValueType.Boolean,
                Model = true
            });
            configuration.ReportTemplates = reports;
            configuration.CommandTemplates = commands;
            return configuration;
        }

        private static IEnumerable<ReportTemplate> SaunaReports(HarviaSauna s, bool energy)
        {
            var celsius = ValueSchema.Number("°C", ValueSemantics.Temperature);
            var minutes = ValueSchema.Integer("min", null, 0);
            yield return Report(s, Power, "on", ValueType.Boolean, s.Active ?? false, ValueSchema.Boolean(ValueSemantics.Switch));
            yield return Report(s, StateKey, "state", ValueType.Text, s.StateName ?? HarviaSauna.Off, Choices(HarviaSauna.StateNames));
            yield return Report(s, Mode, "thermostat mode", ValueType.Number, s.Active == true ? MatterHeat : MatterOff, ModeSchema());
            yield return Report(s, Temperature, "temperature", ValueType.Number, s.Temperature ?? 20, celsius, "°C");
            yield return Report(s, Humidity, "humidity", ValueType.Number, s.Humidity ?? 0, ValueSchema.Number("%", ValueSemantics.Humidity, 0, 100), "%");
            yield return Report(s, TargetTemperature, "target temperature", ValueType.Number, s.TargetTemperature ?? 80, celsius, "°C");
            yield return Report(s, RemainingTime, "remaining time", ValueType.Number, s.RemainingMinutes ?? 0, minutes, "min");
            yield return Report(s, Heating, "heating", ValueType.Boolean, s.Heating ?? false, ValueSchema.Boolean());
            yield return Report(s, Door, "door open", ValueType.Boolean, s.DoorOpen ?? false, ValueSchema.Boolean(ValueSemantics.Door));
            yield return Report(s, Light, "light", ValueType.Boolean, s.Light ?? false, ValueSchema.Boolean(ValueSemantics.Light));
            yield return Report(s, Connected, "connected", ValueType.Boolean, true, ValueSchema.Boolean());
            yield return Report(s, SessionEvent, "session event", ValueType.Text, HarviaSessions.Ended, Choices(HarviaSessions.Events));
            yield return Report(s, LastSession, "last session", ValueType.Entity, null, ValueSchema.Entity(new Dictionary<string, ValueSchema>
            {
                ["startedAt"] = ValueSchema.Text(),
                ["readyAt"] = ValueSchema.Text(),
                ["endedAt"] = ValueSchema.Text(),
                ["durationMinutes"] = ValueSchema.Integer("min"),
                ["heatUpMinutes"] = ValueSchema.Integer("min"),
                ["maxTemperature"] = ValueSchema.Number("°C", ValueSemantics.Temperature),
                ["targetTemperature"] = ValueSchema.Integer("°C"),
                ["energy"] = ValueSchema.Number("kWh", ValueSemantics.Energy)
            }));
            yield return Report(s, SessionsToday, "sessions today", ValueType.Number, 0, ValueSchema.Integer(null, null, 0));
            yield return Report(s, BathingMinutesToday, "bathing minutes today", ValueType.Number, 0, minutes, "min");
            if (energy)
            {
                var kwh = ValueSchema.Number("kWh", ValueSemantics.Energy, 0);
                kwh.Description = "Estimate: the heater's rated power while it heats up";
                yield return Report(s, HeaterPowerNow, "heater power (estimated)", ValueType.Number, 0, ValueSchema.Number("kW", ValueSemantics.Power, 0), "kW");
                yield return Report(s, EnergyLastSession, "energy of the last session (estimated)", ValueType.Number, 0, kwh, "kWh");
                yield return Report(s, EnergyToday, "energy today (estimated)", ValueType.Number, 0, kwh, "kWh");
            }
            yield return Report(s, Ready, "ready", ValueType.Boolean, false, ValueSchema.Boolean());
            yield return Report(s, TimeToReady, "time to ready", ValueType.Number, 0, minutes, "min");
            var readyAt = ValueSchema.Text();
            readyAt.Description = "When the sauna is (or was) ready, as ISO 8601 local time; empty when it is off";
            yield return Report(s, ReadyAt, "ready at", ValueType.Text, "", readyAt);
            yield return Report(s, ScheduleEnabled, "schedule armed", ValueType.Boolean, false, ValueSchema.Boolean());
            var scheduleTime = ValueSchema.Text();
            scheduleTime.Description = "ISO 8601 local time; empty when no schedule is armed";
            yield return Report(s, ScheduleReadyAt, "schedule ready at", ValueType.Text, "", scheduleTime);
            yield return Report(s, ScheduleStartAt, "schedule starts heating at", ValueType.Text, "", scheduleTime);
            yield return Report(s, ScheduleDuration, "schedule duration", ValueType.Number, 0, minutes, "min");
            yield return Report(s, ScheduleTargetTemperature, "schedule target temperature", ValueType.Number, 0, ValueSchema.Integer("°C"), "°C");
        }

        private static IEnumerable<CommandTemplate> SaunaCommands(HarviaSauna s)
        {
            yield return Command(s, Power, "switch on", ValueType.Boolean, true, ValueSchema.Boolean(ValueSemantics.Switch));
            yield return Command(s, TargetTemperature, "set target temperature", ValueType.Number, s.TargetTemperature ?? 80,
                ValueSchema.Number("°C", ValueSemantics.Temperature, MinTemperature, MaxTemperature));
            yield return Command(s, Light, "set light", ValueType.Boolean, true, ValueSchema.Boolean(ValueSemantics.Light));
            yield return Command(s, Mode, "set thermostat mode", ValueType.Number, MatterHeat, ModeSchema());
        }

        private static ValueSchema ModeSchema()
        {
            var schema = ValueSchema.Integer();
            schema.Description = "Matter thermostat SystemMode: 0 off, 4 heat (the sauna on)";
            schema.Enum = [MatterOff, MatterHeat];
            return schema;
        }

        private static ReportTemplate Report(HarviaSauna s, string key, string name, ValueType type, object? model, ValueSchema schema, string? unit = null)
        {
            var template = new ReportTemplate
            {
                Id = Guid.NewGuid().ToString(),
                Address = Address(s.Id, key),
                Name = $"{s.Name} {name}",
                Type = type,
                Model = model,
                Schema = schema
            };
            if (unit != null)
                template.Parameters["unit"] = unit;
            return template;
        }

        private static CommandTemplate Command(HarviaSauna s, string key, string name, ValueType type, object model, ValueSchema schema) => new()
        {
            Id = Guid.NewGuid().ToString(),
            Address = Address(s.Id, key),
            Name = $"{s.Name} {name}",
            Type = type,
            Model = model,
            Schema = schema
        };

        private static ValueSchema Choices(IReadOnlyList<string> names)
        {
            var schema = ValueSchema.Text();
            schema.Enum = names.Cast<object>().ToList();
            return schema;
        }

        /// <summary>
        /// Declares each sauna on the RIoT Control Bridge: a thermostat (cabin temperature, target, off or heat), a
        /// temperature and a humidity sensor, the door as a contact sensor and the light as an on/off light.
        /// </summary>
        /// <remarks>
        /// The bindings are read against the configuration handed in, because <see cref="GetConfigurationTemplate"/>
        /// mints new template ids on every call. The endpoint ids come from the device id, so a sauna stays linked to
        /// the same Matter endpoints across restarts and re-imports. The setpoint command's schema (40-110 °C) gives the
        /// bridge the thermostat's setpoint limits. The separate temperature sensor is for controllers whose thermostat
        /// display stops at about 30 °C.
        /// </remarks>
        public IEnumerable<MatterEndpointTemplate> GetMatterEndpoints(DeviceConfiguration configuration)
        {
            if (configuration?.ReportTemplates == null)
                yield break;

            var ids = configuration.ReportTemplates.Select(r => SplitAddress(r.Address).Id).Where(x => x != null).Select(x => x!).Distinct(StringComparer.Ordinal);
            foreach (var id in ids)
            {
                ReportTemplate? Report(string key) => configuration.ReportTemplates.FirstOrDefault(r => r.Address == Address(id, key));
                CommandTemplate? Command(string key) => configuration.CommandTemplates?.FirstOrDefault(c => c.Address == Address(id, key));
                var sauna = FindSauna(id);
                var power = Report(Power)?.Name;
                var name = sauna?.Name ?? (power != null && power.EndsWith(" on", StringComparison.Ordinal) ? power[..^3] : "Sauna");

                MatterEndpointTemplate Endpoint(string suffix, string what, MatterDeviceType type) => new()
                {
                    Id = suffix.Length == 0 ? $"harvia:{id}" : $"harvia:{id}:{suffix}",
                    Name = what.Length == 0 ? name : $"{name} {what}",
                    DeviceType = type,
                    VendorName = "Harvia",
                    ProductName = "Xenio WiFi"
                };

                var temperature = Report(Temperature);
                var target = Report(TargetTemperature);
                var setpoint = Command(TargetTemperature);
                if (temperature != null && target != null && setpoint != null)
                {
                    var thermostat = Endpoint("", "", MatterDeviceType.Thermostat);
                    thermostat.Attributes.Add(Bind(MatterAttribute.LocalTemperature, temperature, MatterValueScale.CelsiusToHundredths));
                    thermostat.Attributes.Add(Bind(MatterAttribute.OccupiedHeatingSetpoint, target, MatterValueScale.CelsiusToHundredths));
                    thermostat.Commands.Add(new MatterCommandBinding
                    {
                        Attribute = MatterAttribute.OccupiedHeatingSetpoint, CommandTemplateId = setpoint.Id, Scale = MatterValueScale.CelsiusToHundredths
                    });
                    if (Report(Mode) is ReportTemplate mode)
                        thermostat.Attributes.Add(Bind(MatterAttribute.ThermostatSystemMode, mode, MatterValueScale.None));
                    if (Command(Mode) is CommandTemplate modeCommand)
                        thermostat.Commands.Add(new MatterCommandBinding { Attribute = MatterAttribute.ThermostatSystemMode, CommandTemplateId = modeCommand.Id });
                    yield return thermostat;
                }
                if (temperature != null)
                {
                    var sensor = Endpoint("temperature", "temperature", MatterDeviceType.TemperatureSensor);
                    sensor.Attributes.Add(Bind(MatterAttribute.TemperatureMeasuredValue, temperature, MatterValueScale.CelsiusToHundredths));
                    yield return sensor;
                }
                if (Report(Humidity) is ReportTemplate humidity)
                {
                    var sensor = Endpoint("humidity", "humidity", MatterDeviceType.HumiditySensor);
                    sensor.Attributes.Add(Bind(MatterAttribute.HumidityMeasuredValue, humidity, MatterValueScale.PercentToHundredths));
                    yield return sensor;
                }
                if (Report(Door) is ReportTemplate door)
                {
                    // Matter's contact sensor is true when the contact is closed; the report is true when the door is open.
                    var sensor = Endpoint("door", "door", MatterDeviceType.ContactSensor);
                    sensor.Attributes.Add(Bind(MatterAttribute.BooleanStateValue, door, MatterValueScale.InvertBoolean));
                    yield return sensor;
                }
                if (Report(Light) is ReportTemplate light)
                {
                    var lamp = Endpoint("light", "light", MatterDeviceType.OnOffLight);
                    lamp.Attributes.Add(Bind(MatterAttribute.OnOff, light, MatterValueScale.None));
                    if (Command(Light) is CommandTemplate switchLight)
                        lamp.Commands.Add(new MatterCommandBinding { Attribute = MatterAttribute.OnOff, CommandTemplateId = switchLight.Id });
                    yield return lamp;
                }
            }
        }

        private static MatterAttributeBinding Bind(MatterAttribute attribute, ReportTemplate report, MatterValueScale scale) => new()
        {
            Attribute = attribute,
            ReportTemplateId = report.Id,
            Scale = scale
        };
    }
}
