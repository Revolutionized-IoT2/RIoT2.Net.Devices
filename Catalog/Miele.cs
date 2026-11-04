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
using RIoT2.Core.Utils;
using RIoT2.Net.Devices.Models;
using RIoT2.Net.Devices.Services;
using RIoT2.Net.Devices.Services.Miele;
using ValueType = RIoT2.Core.ValueType;

namespace RIoT2.Net.Devices.Catalog
{
    /// <summary>The <see cref="Miele"/> device's parameters: the app registered at developer.miele.com and the Miele login.</summary>
    public sealed class MieleOptions
    {
        [DeviceParameter("clientId", Required = true, Description = "Client id of the app registered at developer.miele.com")]
        public string ClientId { get; set; } = "";

        [DeviceParameter("clientSecret", Required = true, Secret = true, Description = "Client secret of the app registered at developer.miele.com")]
        public string ClientSecret { get; set; } = "";

        [DeviceParameter("email", Description = "E-mail address of the Miele account, for logging in without a browser")]
        public string Email { get; set; } = "";

        [DeviceParameter("password", Secret = true, Description = "Password of the Miele account, for logging in without a browser")]
        public string Password { get; set; } = "";

        [DeviceParameter("country", Default = "fi-FI", Description = "Country of the Miele account, e.g. fi-FI, de-DE or en-GB")]
        public string Country { get; set; } = "fi-FI";

        [DeviceParameter("authorizationCode", Secret = true,
            Description = "Instead of the e-mail and password: the code (or the whole address) Miele's login page sends the browser to")]
        public string AuthorizationCode { get; set; } = "";

        [DeviceParameter("redirectUri", Default = "http://localhost/miele", Description = "Redirect URI registered for the app, for the authorization code")]
        public string RedirectUri { get; set; } = "http://localhost/miele";

        [DeviceParameter("language", Default = "en", Description = "Language of the program, phase and status names, e.g. en, fi or de")]
        public string Language { get; set; } = "en";
    }

    /// <summary>
    /// Miele appliances through the Miele 3rd Party API. Miele pushes their state on an event stream; an hourly poll
    /// reads it again. Reports each appliance's status, program, times, temperatures, signals, TwinDos levels, program
    /// events and the energy and water of each run and day; commands start, stop, pause, delay, switch on or off, select
    /// a program and set the temperature. Oven temperatures are Matter temperature sensors.
    /// </summary>
    [RequiresHardware("Miele 3rd Party API")]
    public class Miele : AsyncDeviceBase<MieleOptions>, IDeviceWithConfiguration, IAsyncCommandDevice, IMatterDevice
    {
        // Report and command keys; an address is "<fabrication number>|<key>".
        internal const string Status = "status";
        internal const string StatusCode = "statusCode";
        internal const string Program = "program";
        internal const string ProgramId = "programId";
        internal const string ProgramPhase = "programPhase";
        internal const string RemainingTime = "remainingTime";
        internal const string ElapsedTime = "elapsedTime";
        internal const string StartDelay = "startDelay";
        internal const string FinishTime = "finishTime";
        internal const string Door = "door";
        internal const string Failure = "failure";
        internal const string Info = "info";
        internal const string RemoteStart = "remoteStart";
        internal const string Connected = "connected";
        internal const string Temperature = "temperature";
        internal const string TargetTemperature = "targetTemperature";
        internal const string CoreTemperature = "coreTemperature";
        internal const string CoreTargetTemperature = "coreTargetTemperature";
        internal const string SpinningSpeed = "spinningSpeed";
        internal const string EnergyForecast = "energyForecast";
        internal const string WaterForecast = "waterForecast";
        internal const string TwinDos1Level = "twinDos1Level";
        internal const string TwinDos2Level = "twinDos2Level";
        internal const string ProgramEvent = "programEvent";
        internal const string LastRun = "lastRun";
        internal const string EnergyLastRun = "energyLastRun";
        internal const string WaterLastRun = "waterLastRun";
        internal const string EnergyToday = "energyToday";
        internal const string WaterToday = "waterToday";
        internal const string StartCommand = "start";
        internal const string StopCommand = "stop";
        internal const string PauseCommand = "pause";
        internal const string StartAt = "startAt";
        internal const string Power = "power";
        internal const string RefreshAddress = "refresh";

        /// <summary>The shortest time between two polls.</summary>
        internal static readonly TimeSpan MinimumPollInterval = TimeSpan.FromMinutes(1);

        /// <summary>How often the programs are read again.</summary>
        internal static readonly TimeSpan ProgramInterval = TimeSpan.FromDays(1);

        private static readonly int[] _finishing = [MieleCodes.Programmed, MieleCodes.WaitingToStart, MieleCodes.Running, MieleCodes.Pause, MieleCodes.RinseHold];

        private readonly object _lock = new();
        // Applies one appliance state at a time: the event stream and a poll don't interleave runs and reports.
        private readonly object _applyLock = new();
        private readonly Dictionary<string, MieleAppliance> _appliances = [];
        private readonly Dictionary<string, MieleActions> _actions = [];
        private readonly Dictionary<string, List<MieleProgram>> _programs = [];
        private readonly Dictionary<string, (int? TwinDos1, int? TwinDos2)> _filling = [];
        private readonly Dictionary<string, MieleRunState> _runs = [];
        private MieleClient? _client;
        private CancellationTokenSource? _listenerStop;
        private Task? _listener;
        private DateTimeOffset? _lastPoll;
        private DateTimeOffset? _programsRead;

        public Miele(ILogger logger) : base(logger) { }

        /// <summary>The folder of the token and run files; tests replace it.</summary>
        internal string DataDirectory { get; set; } = "Data";

        /// <summary>The clock; tests replace it.</summary>
        internal TimeProvider Time { get; set; } = TimeProvider.System;

        /// <summary>Whether to listen to Miele's event stream; tests that only poll switch it off.</summary>
        internal bool ListenToEvents { get; set; } = true;

        /// <summary>The first wait before the event stream is opened again; it doubles up to <see cref="MaxReconnectDelay"/>.</summary>
        internal TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(5);

        internal TimeSpan MaxReconnectDelay { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>How long the event stream may be silent; Miele pings every 20 seconds.</summary>
        internal TimeSpan EventTimeout { get; set; } = TimeSpan.FromSeconds(120);

        /// <summary>The running event stream listener, for tests.</summary>
        internal Task? Listener => _listener;

        /// <summary>What Miele allows the appliance to do now, as last read or pushed.</summary>
        internal MieleActions ActionsFor(string id)
        {
            lock (_lock)
                return _actions.GetValueOrDefault(id) ?? new MieleActions();
        }

        // The HTTP call; the tests replace it.
        internal virtual Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption completion, CancellationToken cancellationToken) =>
            DirectHttp.SendAsync(request, "Miele", cancellationToken, completion);

        public override void ConfigureDevice()
        {
            _client = null;
            _lastPoll = null;
            _programsRead = null;
            lock (_lock)
            {
                _appliances.Clear();
                _actions.Clear();
                _programs.Clear();
                _filling.Clear();
                _runs.Clear();
            }
        }

        protected override async Task StartDeviceAsync(CancellationToken cancellationToken)
        {
            var settings = new MieleAuthSettings(Options.ClientId.Trim(), Options.ClientSecret.Trim(), Options.Email?.Trim(), Options.Password,
                string.IsNullOrWhiteSpace(Options.Country) ? "fi-FI" : Options.Country.Trim(), Options.AuthorizationCode,
                string.IsNullOrWhiteSpace(Options.RedirectUri) ? "http://localhost/miele" : Options.RedirectUri.Trim());
            var auth = new MieleAuth(settings, SendAsync, TokenFile(settings.ClientId), Logger, Time);
            var language = string.IsNullOrWhiteSpace(Options.Language) ? "en" : Options.Language.Trim();
            _client = new MieleClient(auth, language, SendAsync, Logger);

            // The first poll's errors, a missing login too, put the device into the error state with the reason. A
            // configured device outlives a Miele outage at start: the event stream and the next refresh try again.
            try
            {
                await PollAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (MieleUnavailableException error) when (ReportTemplates?.Any() == true || CommandTemplates?.Any() == true)
            {
                Logger.LogWarning("Miele is unavailable; the event stream and the next refresh try again: {Reason}", error.Message);
            }

            if (ListenToEvents)
            {
                _listenerStop = new CancellationTokenSource();
                _listener = Task.Run(() => ListenAsync(_client, _listenerStop.Token), CancellationToken.None);
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
                    Logger.LogWarning("Miele's event stream listener didn't stop in 10 seconds.");
                }
            }
            stop.Dispose();
        }

        // Called by the scheduler; the refresh is defined on the device, so the report is null.
        protected override Task RefreshAsync(ReportTemplate? report, CancellationToken cancellationToken) =>
            report != null ? Task.CompletedTask : RefreshAppliancesAsync(cancellationToken);

        private async Task RefreshAppliancesAsync(CancellationToken cancellationToken)
        {
            if (_lastPoll is DateTimeOffset last && Time.GetUtcNow() - last < MinimumPollInterval)
            {
                Logger.LogDebug("Miele was polled less than a minute ago; the refresh is skipped.");
                return;
            }

            try
            {
                await PollAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (MieleUnavailableException error)
            {
                Logger.LogWarning("Could not refresh Miele: {Reason}", error.Message);
            }
        }

        private async Task PollAsync(CancellationToken cancellationToken)
        {
            var client = _client ?? throw new DeviceNotReadyException("The Miele device isn't started.");
            var devices = await client.GetDevicesAsync(cancellationToken).ConfigureAwait(false);

            var actions = new Dictionary<string, MieleActions>();
            foreach (var id in devices.Keys)
                actions[id] = await Optional(() => client.GetActionsAsync(id, cancellationToken), new MieleActions(), "actions").ConfigureAwait(false);

            var now = Time.GetUtcNow();
            Dictionary<string, List<MieleProgram>>? programs = null;
            bool known;
            lock (_lock)
                known = devices.Keys.All(_programs.ContainsKey);
            if (!known || _programsRead is not DateTimeOffset readAt || now - readAt >= ProgramInterval)
            {
                programs = [];
                foreach (var id in devices.Keys)
                    programs[id] = await Optional(() => client.GetProgramsAsync(id, cancellationToken), [], "programs").ConfigureAwait(false);
                _programsRead = now;
            }
            var filling = await Optional(() => client.GetFillingLevelsAsync(cancellationToken), [], "filling levels").ConfigureAwait(false);

            lock (_lock)
            {
                foreach (var (id, allowed) in actions)
                    _actions[id] = allowed;
                foreach (var (id, list) in programs ?? new Dictionary<string, List<MieleProgram>>())
                    _programs[id] = list;
                _filling.Clear();
                foreach (var (id, levels) in filling)
                    _filling[id] = levels;
                foreach (var id in _appliances.Keys.Where(x => !devices.ContainsKey(x)).ToList())
                    _appliances.Remove(id);
            }
            _lastPoll = now;

            if (devices.Count == 0)
                Logger.LogWarning("The Miele account has no appliances.");
            foreach (var (id, json) in devices)
                ApplyDevice(id, json);
        }

        // A read that some appliances don't support (4xx): the default instead.
        private async Task<T> Optional<T>(Func<Task<T>> read, T fallback, string what)
        {
            try
            {
                return await read().ConfigureAwait(false);
            }
            catch (MieleRefusedException error)
            {
                Logger.LogDebug("Miele refused the {What}: {Reason}", what, error.Message);
                return fallback;
            }
        }

        private async Task ListenAsync(MieleClient client, CancellationToken cancellationToken)
        {
            var delay = ReconnectDelay;
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    using var response = await client.OpenEventsAsync(cancellationToken).ConfigureAwait(false);
                    using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
                    Logger.LogDebug("Miele's event stream is open.");
                    await foreach (var item in MieleClient.ReadEventsAsync(reader, EventTimeout, cancellationToken).ConfigureAwait(false))
                    {
                        delay = ReconnectDelay;
                        HandleEvent(item);
                    }
                    Logger.LogDebug("Miele closed the event stream; opening it again.");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (MieleAuthenticationException error)
                {
                    Logger.LogError("Miele's event stream needs a login: {Reason}", error.Message);
                    delay = MaxReconnectDelay;
                }
                catch (Exception error)
                {
                    Logger.LogWarning("Miele's event stream failed; opening it again in {Delay}: {Reason}", delay, error.Message);
                }

                try
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxReconnectDelay.Ticks));
            }
        }

        internal void HandleEvent(MieleEvent item)
        {
            if (item.Type is not ("devices" or "actions"))
                return;
            JsonNode? json;
            try
            {
                json = JsonNode.Parse(item.Data);
            }
            catch (JsonException error)
            {
                Logger.LogWarning("Miele sent a {Type} event that isn't JSON: {Reason}", item.Type, error.Message);
                return;
            }

            if (item.Type == "devices")
            {
                foreach (var (id, device) in MieleClient.Devices(json))
                    ApplyDevice(id, device);
                return;
            }
            lock (_lock)
            {
                foreach (var (id, actions) in (json as JsonObject) ?? new JsonObject())
                    _actions[id] = MieleActions.Parse(actions as JsonObject);
            }
        }

        /// <summary>
        /// Takes one appliance's state, from a poll or the event stream: follows its run, keeps the day's totals and
        /// sends the reports.
        /// </summary>
        private void ApplyDevice(string id, JsonObject json)
        {
            var appliance = MieleAppliance.Parse(id, json);
            lock (_applyLock)
            {
                lock (_lock)
                {
                    if (_filling.TryGetValue(id, out var levels))
                        (appliance.TwinDos1Level, appliance.TwinDos2Level) = levels;
                    _appliances[id] = appliance;
                }

                var now = Time.GetUtcNow();
                var day = TimeZoneInfo.ConvertTime(now, Time.LocalTimeZone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                var state = RunState(id);
                var update = MieleRuns.Update(state, appliance, now, day);
                if (update.Changed)
                    SaveRunState(id, state);

                SendApplianceReports(appliance, state, now);
                if (update.Event != null)
                    SendAlways(id, ProgramEvent, new ValueModel(update.Event), now);
                if (update.Finished is MieleRun run)
                    SendRunReports(appliance, run);
            }
        }

        public async Task ExecuteCommandAsync(string commandId, string value, CancellationToken cancellationToken)
        {
            var command = CommandTemplates?.FirstOrDefault(x => x.Id == commandId) ?? throw new UnknownCommandException(commandId);
            if (string.Equals(command.Address, RefreshAddress, StringComparison.OrdinalIgnoreCase))
            {
                await RefreshAppliancesAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            var (id, key) = SplitAddress(command.Address);
            if (id == null)
                throw new UnknownCommandException(commandId);

            var client = _client;
            var appliance = FindAppliance(id);
            if (appliance == null && client != null && _lastPoll == null)
            {
                // Miele was unavailable at start: read the appliances now.
                await PollAsync(cancellationToken).ConfigureAwait(false);
                appliance = FindAppliance(id);
            }
            if (client == null || appliance == null)
                throw new DeviceNotReadyException($"Miele appliance {id} isn't loaded yet.");

            MieleActions actions;
            List<MieleProgram> programs;
            lock (_lock)
            {
                actions = _actions.GetValueOrDefault(id) ?? new MieleActions();
                programs = _programs.GetValueOrDefault(id) ?? [];
            }

            var request = ParseCommand(key, value, appliance, actions, programs, Time.GetUtcNow()) ?? throw new UnknownCommandException(commandId);
            try
            {
                if (request.IsProgram)
                    await client.SetProgramAsync(id, request.Body, cancellationToken).ConfigureAwait(false);
                else
                    await client.SendActionAsync(id, request.Body, cancellationToken).ConfigureAwait(false);
            }
            catch (MieleRefusedException error)
            {
                throw new InvalidOperationException($"Miele refused the command: {error.Message}", error);
            }

            // What is allowed changes with the command; the state itself comes on the event stream.
            try
            {
                var allowed = await client.GetActionsAsync(id, cancellationToken).ConfigureAwait(false);
                lock (_lock)
                    _actions[id] = allowed;
            }
            catch (Exception error) when (error is MieleRefusedException or MieleUnavailableException)
            {
                Logger.LogDebug("Could not read the actions of {Appliance} after the command: {Reason}", appliance.Name, error.Message);
            }
        }

        /// <summary>A command's request: the body for the actions or the programs endpoint.</summary>
        internal sealed record MieleCommand(JsonObject Body, bool IsProgram = false);

        /// <summary>
        /// The request a command sends, or null for an unknown key. A value that doesn't fit throws
        /// <see cref="ArgumentException"/>; a command the appliance doesn't allow now throws
        /// <see cref="InvalidOperationException"/> with the reason.
        /// </summary>
        internal static MieleCommand? ParseCommand(string key, string value, MieleAppliance appliance, MieleActions actions,
            IReadOnlyList<MieleProgram> programs, DateTimeOffset now)
        {
            switch (key)
            {
                case StartCommand:
                case StopCommand:
                case PauseCommand:
                    if (!CommandBool(value))
                        throw new ArgumentException($"Send true to {key} the appliance.", nameof(value));
                    var code = key switch { StartCommand => MieleCodes.Start, StopCommand => MieleCodes.Stop, _ => MieleCodes.PauseAction };
                    if (!actions.ProcessActions.Contains(code))
                        throw new InvalidOperationException(key == StartCommand
                            ? $"Miele doesn't allow starting the {appliance.Name} now ({appliance.StatusName ?? "unknown"}). Switch on remote start (MobileStart) on the appliance and choose a program on it."
                            : NotAllowed(appliance, key == StopCommand ? "stopping" : "pausing"));
                    return new MieleCommand(new JsonObject { ["processAction"] = code });
                case StartDelay:
                    return Delay(appliance, actions, WholeNumber(value, "start delay"));
                case StartAt:
                    var text = CommandText(value).Trim();
                    if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
                        throw new ArgumentException($"'{text}' is not a date and time, e.g. 2026-11-05T02:00:00+02:00.", nameof(value));
                    var minutes = (int)Math.Ceiling((at - now).TotalMinutes);
                    if (minutes < 0)
                        throw new ArgumentOutOfRangeException(nameof(value), text, "The start time is in the past.");
                    return Delay(appliance, actions, minutes);
                case Power:
                    var on = CommandBool(value);
                    if (on ? !actions.PowerOn : !actions.PowerOff)
                        throw new InvalidOperationException(NotAllowed(appliance, on ? "switching on" : "switching off"));
                    return new MieleCommand(new JsonObject { [on ? "powerOn" : "powerOff"] = true });
                case Program:
                    return SelectProgram(value, appliance, programs);
                case TargetTemperature:
                    var range = actions.TargetTemperatures.FirstOrDefault(x => x.Zone == 1)
                        ?? throw new InvalidOperationException(NotAllowed(appliance, "setting the temperature"));
                    var temperature = Math.Round(CommandNumber(value), MidpointRounding.AwayFromZero);
                    if (temperature < (range.Min ?? double.MinValue) || temperature > (range.Max ?? double.MaxValue))
                        throw new ArgumentOutOfRangeException(nameof(value), temperature,
                            $"The temperature must be {Format(range.Min ?? temperature)}-{Format(range.Max ?? temperature)} °C.");
                    return new MieleCommand(new JsonObject
                    {
                        ["targetTemperature"] = new JsonArray(new JsonObject { ["zone"] = 1, ["value"] = (int)temperature })
                    });
                default:
                    return null;
            }
        }

        private static MieleCommand Delay(MieleAppliance appliance, MieleActions actions, int minutes)
        {
            if (actions.StartDelayMin is not int min || actions.StartDelayMax is not int max)
                throw new InvalidOperationException(NotAllowed(appliance, "a delayed start") +
                    " Choose a program on the appliance and switch on remote start (MobileStart).");
            if (minutes < min || minutes > max)
                throw new ArgumentOutOfRangeException(nameof(minutes), minutes, $"The start delay must be {min}-{max} minutes.");
            return new MieleCommand(new JsonObject { ["startTime"] = HoursMinutes(minutes) });
        }

        // A program by name or id, or {"program": ..., "temperature": ..., "duration": minutes}.
        private static MieleCommand SelectProgram(string value, MieleAppliance appliance, IReadOnlyList<MieleProgram> programs)
        {
            if (programs.Count == 0)
                throw new InvalidOperationException($"Miele doesn't let the {appliance.Name}'s program be chosen remotely.");
            var json = CommandJson(value);
            JsonElement? temperatureJson = null, durationJson = null;
            string name;
            if (json.ValueKind == JsonValueKind.Object)
            {
                if (!json.TryGetProperty("program", out var programJson))
                    throw new ArgumentException("The value must be a program, or {\"program\": ..., \"temperature\": ..., \"duration\": minutes}.", nameof(value));
                name = Text(programJson);
                if (json.TryGetProperty("temperature", out var t) && t.ValueKind != JsonValueKind.Null)
                    temperatureJson = t;
                if (json.TryGetProperty("duration", out var d) && d.ValueKind != JsonValueKind.Null)
                    durationJson = d;
            }
            else
            {
                name = Text(json);
            }

            var program = programs.FirstOrDefault(x => string.Equals(x.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? programs.FirstOrDefault(x => x.Id.ToString(CultureInfo.InvariantCulture) == name.Trim())
                ?? throw new ArgumentException($"The program must be one of {string.Join(", ", programs.Select(x => x.Name))}.", nameof(value));

            var body = new JsonObject { ["programId"] = program.Id };
            if (temperatureJson is JsonElement temperatureElement)
            {
                if (program.TemperatureMin == null && program.TemperatureMax == null)
                    throw new ArgumentException($"The program {program.Name} takes no temperature.", nameof(value));
                var temperature = Number(temperatureElement, "temperature");
                if (program.TemperatureStep is double step)
                    temperature = Math.Round(Math.Round(temperature / step, MidpointRounding.AwayFromZero) * step, 1);
                if (temperature < (program.TemperatureMin ?? double.MinValue) || temperature > (program.TemperatureMax ?? double.MaxValue))
                    throw new ArgumentOutOfRangeException(nameof(value), temperature,
                        $"The temperature of {program.Name} must be {Format(program.TemperatureMin ?? temperature)}-{Format(program.TemperatureMax ?? temperature)} °C.");
                body["temperature"] = temperature;
            }
            else if (program.TemperatureMandatory)
            {
                throw new ArgumentException($"The program {program.Name} needs a temperature.", nameof(value));
            }

            if (durationJson is JsonElement durationElement)
            {
                if (program.DurationMin == null && program.DurationMax == null)
                    throw new ArgumentException($"The program {program.Name} takes no duration.", nameof(value));
                var duration = Number(durationElement, "duration");
                if (duration != Math.Floor(duration) || duration < (program.DurationMin ?? 0) || duration > (program.DurationMax ?? int.MaxValue))
                    throw new ArgumentOutOfRangeException(nameof(value), duration,
                        $"The duration of {program.Name} must be {program.DurationMin ?? 0}-{program.DurationMax} whole minutes.");
                body["duration"] = HoursMinutes((int)duration);
            }
            else if (program.DurationMandatory)
            {
                throw new ArgumentException($"The program {program.Name} needs a duration.", nameof(value));
            }
            return new MieleCommand(body, IsProgram: true);
        }

        private static string NotAllowed(MieleAppliance appliance, string what) =>
            $"Miele doesn't allow {what} the {appliance.Name} now ({appliance.StatusName ?? "unknown"}).";

        private static JsonArray HoursMinutes(int minutes) => new(minutes / 60, minutes % 60);

        private static string Text(JsonElement json) => json.ValueKind switch
        {
            JsonValueKind.String => json.GetString() ?? "",
            JsonValueKind.Number => json.GetRawText(),
            _ => throw new ArgumentException("The program is a name or an id.")
        };

        private static double Number(JsonElement json, string what)
        {
            if (json.ValueKind == JsonValueKind.Number)
                return json.GetDouble();
            if (json.ValueKind == JsonValueKind.String &&
                double.TryParse(json.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                return number;
            throw new ArgumentException($"The {what} is a number.");
        }

        // A command value arrives as JSON: true, 45, "2026-11-05T02:00:00+02:00", {...}. A bare word is taken as text.
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

        private static int WholeNumber(string value, string what)
        {
            var number = CommandNumber(value);
            if (number != Math.Floor(number) || number < 0 || number > int.MaxValue)
                throw new ArgumentException($"The {what} is a whole number of minutes.", nameof(value));
            return (int)number;
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

        private static string CommandText(string value)
        {
            var json = CommandJson(value);
            return json.ValueKind == JsonValueKind.String ? json.GetString() ?? "" : value;
        }

        private static string Format(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);

        internal static string Address(string id, string key) => $"{id}|{key}";

        private static (string? Id, string Key) SplitAddress(string? address)
        {
            var separator = address?.LastIndexOf('|') ?? -1;
            return separator <= 0 ? (null, "") : (address![..separator], address[(separator + 1)..]);
        }

        private MieleAppliance? FindAppliance(string id)
        {
            lock (_lock)
                return _appliances.GetValueOrDefault(id);
        }

        private ReportTemplate? FindTemplate(string id, string key)
        {
            var address = Address(id, key);
            return ReportTemplates?.FirstOrDefault(x => string.Equals(x.Address, address, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>When the running or programmed program ends, as local time; empty when none is.</summary>
        internal string FinishTimeText(MieleAppliance appliance, DateTimeOffset now)
        {
            if (appliance.StatusCode is not int status || !_finishing.Contains(status) || appliance.RemainingMinutes is not int remaining || remaining <= 0)
                return "";
            var finish = TimeZoneInfo.ConvertTime(now.AddMinutes((appliance.StartDelayMinutes ?? 0) + remaining), Time.LocalTimeZone);
            finish = finish.AddSeconds(-finish.Second).AddMilliseconds(-finish.Millisecond);
            return finish.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
        }

        private IEnumerable<(string Key, object? Value)> Values(MieleAppliance appliance, MieleRunState state, DateTimeOffset now)
        {
            if (appliance.StatusCode != null)
            {
                yield return (Status, appliance.StatusName);
                yield return (StatusCode, appliance.StatusCode);
                yield return (Program, appliance.Program ?? "");
                yield return (ProgramPhase, appliance.ProgramPhase ?? "");
                yield return (FinishTime, FinishTimeText(appliance, now));
                yield return (Connected, appliance.Connected);
            }
            yield return (ProgramId, appliance.ProgramId);
            yield return (RemainingTime, appliance.RemainingMinutes);
            yield return (ElapsedTime, appliance.ElapsedMinutes);
            yield return (StartDelay, appliance.StartDelayMinutes);
            yield return (Door, appliance.Door);
            yield return (Failure, appliance.Failure);
            yield return (Info, appliance.Info);
            yield return (RemoteStart, appliance.RemoteStart);
            yield return (Temperature, appliance.Temperature);
            yield return (TargetTemperature, appliance.TargetTemperature);
            yield return (CoreTemperature, appliance.CoreTemperature);
            yield return (CoreTargetTemperature, appliance.CoreTargetTemperature);
            yield return (SpinningSpeed, appliance.SpinningSpeed);
            yield return (EnergyForecast, appliance.EnergyForecast);
            yield return (WaterForecast, appliance.WaterForecast);
            yield return (TwinDos1Level, appliance.TwinDos1Level);
            yield return (TwinDos2Level, appliance.TwinDos2Level);
            if (MieleCodes.HasConsumption(appliance.Type))
            {
                yield return (EnergyToday, state.EnergyToday);
                yield return (WaterToday, state.WaterToday);
            }
        }

        private void SendApplianceReports(MieleAppliance appliance, MieleRunState state, DateTimeOffset now)
        {
            var timeStamp = now.ToUnixTimeSeconds();
            foreach (var (key, value) in Values(appliance, state, now))
            {
                if (value == null || FindTemplate(appliance.Id, key) is not ReportTemplate template)
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

        // Each run once, time-stamped at its end; equal runs are all kept.
        private void SendRunReports(MieleAppliance appliance, MieleRun run)
        {
            var lastRun = new Dictionary<string, object?>
            {
                ["program"] = run.Program ?? "",
                ["startedAt"] = Local(run.StartedAt),
                ["endedAt"] = Local(run.EndedAt),
                ["durationMinutes"] = run.DurationMinutes,
                ["energy"] = run.Energy,
                ["water"] = run.Water
            };
            SendAlways(appliance.Id, LastRun, new ValueModel(lastRun), run.EndedAt);
            if (run.Energy is double energy)
                SendAlways(appliance.Id, EnergyLastRun, new ValueModel(energy), run.EndedAt);
            if (run.Water is double water)
                SendAlways(appliance.Id, WaterLastRun, new ValueModel(water), run.EndedAt);
            Logger.LogInformation("Miele {Appliance} ended {Program} after {Minutes} min: {Energy} kWh, {Water} l.", appliance.Name,
                run.Program ?? "a program", run.DurationMinutes, run.Energy?.ToString(CultureInfo.InvariantCulture) ?? "-",
                run.Water?.ToString(CultureInfo.InvariantCulture) ?? "-");
        }

        private void SendAlways(string id, string key, ValueModel value, DateTimeOffset at)
        {
            if (FindTemplate(id, key) is not ReportTemplate template)
                return;
            SendReport(this, new Report { Id = template.Id, TimeStamp = at.ToUnixTimeSeconds(), Value = value, Filter = "" });
        }

        private string Local(DateTimeOffset time) =>
            TimeZoneInfo.ConvertTime(time, Time.LocalTimeZone).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

        internal string TokenFile(string clientId) => Path.Combine(DataDirectory, $"miele-auth-{Hash(clientId)}.json");

        internal string RunFile(string id) => Path.Combine(DataDirectory, $"miele-{Hash(id)}.json");

        private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

        private MieleRunState RunState(string id)
        {
            lock (_lock)
            {
                if (_runs.TryGetValue(id, out var cached))
                    return cached;
            }
            var file = RunFile(id);
            MieleRunState? state = null;
            try
            {
                state = File.Exists(file) ? JsonSerializer.Deserialize<MieleRunState>(File.ReadAllText(file)) : null;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                Logger.LogWarning(error, "Could not read the Miele run file {File}; today's totals count from now.", file);
            }
            state ??= new MieleRunState();
            lock (_lock)
                _runs[id] = state;
            return state;
        }

        private void SaveRunState(string id, MieleRunState state)
        {
            var file = RunFile(id);
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
                Logger.LogWarning(error, "Could not save the Miele run file {File}.", file);
            }
        }

        public DeviceConfiguration GetConfigurationTemplate()
        {
            var configuration = new DeviceConfiguration
            {
                Id = Guid.NewGuid().ToString(),
                Name = "Miele",
                ClassFullName = GetType().FullName!,
                // Every hour, eleven minutes past: a safety poll; the event stream brings the changes.
                RefreshSchedule = "0 11 * ? * *",
                DeviceParameters = new Dictionary<string, string>
                {
                    ["clientId"] = "",
                    ["clientSecret"] = "",
                    ["email"] = "user@example.com",
                    ["password"] = "",
                    ["country"] = "fi-FI",
                    ["authorizationCode"] = "",
                    ["redirectUri"] = "http://localhost/miele",
                    ["language"] = "en"
                },
                ReportTemplates = [],
                CommandTemplates = []
            };

            if (State != DeviceState.Running)
                return configuration;

            List<MieleAppliance> appliances;
            Dictionary<string, MieleActions> actions;
            Dictionary<string, List<MieleProgram>> programs;
            lock (_lock)
            {
                appliances = _appliances.Values.OrderBy(x => x.Name, StringComparer.Ordinal).ToList();
                actions = new Dictionary<string, MieleActions>(_actions);
                programs = new Dictionary<string, List<MieleProgram>>(_programs);
            }

            var reports = new List<ReportTemplate>();
            var commands = new List<CommandTemplate>();
            foreach (var appliance in appliances)
            {
                reports.AddRange(ApplianceReports(appliance));
                commands.AddRange(ApplianceCommands(appliance, actions.GetValueOrDefault(appliance.Id), programs.GetValueOrDefault(appliance.Id) ?? []));
            }
            commands.Add(new CommandTemplate
            {
                Id = Guid.NewGuid().ToString(),
                Name = "Refresh Miele",
                Address = RefreshAddress,
                Type = ValueType.Boolean,
                Model = true
            });
            configuration.ReportTemplates = reports;
            configuration.CommandTemplates = commands;
            return configuration;
        }

        private static IEnumerable<ReportTemplate> ApplianceReports(MieleAppliance a)
        {
            var minutes = ValueSchema.Integer("min", null, 0);
            yield return Report(a, Status, "status", ValueType.Text, a.StatusName ?? "off", Choices(MieleCodes.StatusNames));
            yield return Report(a, StatusCode, "status code", ValueType.Number, a.StatusCode ?? MieleCodes.Off, ValueSchema.Integer());
            yield return Report(a, Program, "program", ValueType.Text, a.Program ?? "", ValueSchema.Text());
            yield return Report(a, ProgramId, "program id", ValueType.Number, a.ProgramId ?? 0, ValueSchema.Integer());
            yield return Report(a, ProgramPhase, "program phase", ValueType.Text, a.ProgramPhase ?? "", ValueSchema.Text());
            yield return Report(a, RemainingTime, "remaining time", ValueType.Number, a.RemainingMinutes ?? 0, minutes, "min");
            yield return Report(a, ElapsedTime, "elapsed time", ValueType.Number, a.ElapsedMinutes ?? 0, minutes, "min");
            yield return Report(a, StartDelay, "start delay", ValueType.Number, a.StartDelayMinutes ?? 0, minutes, "min");
            var finish = ValueSchema.Text();
            finish.Description = "When the program ends, as ISO 8601 local time; empty when none is running or programmed";
            yield return Report(a, FinishTime, "finish time", ValueType.Text, "", finish);
            yield return Report(a, Door, "door open", ValueType.Boolean, a.Door ?? false, ValueSchema.Boolean(ValueSemantics.Door));
            yield return Report(a, Failure, "failure", ValueType.Boolean, a.Failure ?? false, ValueSchema.Boolean());
            yield return Report(a, Info, "info", ValueType.Boolean, a.Info ?? false, ValueSchema.Boolean());
            yield return Report(a, RemoteStart, "remote start enabled", ValueType.Boolean, a.RemoteStart ?? false, ValueSchema.Boolean());
            yield return Report(a, Connected, "connected", ValueType.Boolean, a.Connected, ValueSchema.Boolean());
            yield return Report(a, ProgramEvent, "program event", ValueType.Text, MieleRuns.Finished, Choices(MieleRuns.Events));
            yield return Report(a, LastRun, "last run", ValueType.Entity, null, ValueSchema.Entity(new Dictionary<string, ValueSchema>
            {
                ["program"] = ValueSchema.Text(),
                ["startedAt"] = ValueSchema.Text(),
                ["endedAt"] = ValueSchema.Text(),
                ["durationMinutes"] = ValueSchema.Integer("min"),
                ["energy"] = ValueSchema.Number("kWh", ValueSemantics.Energy),
                ["water"] = ValueSchema.Number("l")
            }));

            var celsius = ValueSchema.Number("°C", ValueSemantics.Temperature);
            var oven = MieleCodes.IsOven(a.Type);
            var washer = MieleCodes.IsWasher(a.Type);
            if (oven || a.Temperature != null)
                yield return Report(a, Temperature, "temperature", ValueType.Number, a.Temperature ?? 20, celsius, "°C");
            if (oven || washer || a.TargetTemperature != null)
                yield return Report(a, TargetTemperature, "target temperature", ValueType.Number, a.TargetTemperature ?? 0, celsius, "°C");
            if (a.HasCoreProbe)
            {
                yield return Report(a, CoreTemperature, "food probe temperature", ValueType.Number, a.CoreTemperature ?? 20, celsius, "°C");
                yield return Report(a, CoreTargetTemperature, "food probe target temperature", ValueType.Number, a.CoreTargetTemperature ?? 0, celsius, "°C");
            }
            if (washer)
                yield return Report(a, SpinningSpeed, "spinning speed", ValueType.Number, a.SpinningSpeed ?? 0, ValueSchema.Integer("rpm", null, 0), "rpm");
            if (a.TwinDos1Level != null)
                yield return Report(a, TwinDos1Level, "TwinDos 1 level", ValueType.Number, a.TwinDos1Level.Value, ValueSchema.Integer("%", null, 0, 100), "%");
            if (a.TwinDos2Level != null)
                yield return Report(a, TwinDos2Level, "TwinDos 2 level", ValueType.Number, a.TwinDos2Level.Value, ValueSchema.Integer("%", null, 0, 100), "%");

            if (MieleCodes.HasConsumption(a.Type))
            {
                var kwh = ValueSchema.Number("kWh", ValueSemantics.Energy, 0);
                var litres = ValueSchema.Number("l", null, 0);
                var percent = ValueSchema.Number("%", null, 0, 100);
                yield return Report(a, EnergyLastRun, "energy of the last run", ValueType.Number, 0, kwh, "kWh");
                yield return Report(a, WaterLastRun, "water of the last run", ValueType.Number, 0, litres, "l");
                yield return Report(a, EnergyToday, "energy today", ValueType.Number, 0, kwh, "kWh");
                yield return Report(a, WaterToday, "water today", ValueType.Number, 0, litres, "l");
                yield return Report(a, EnergyForecast, "energy forecast", ValueType.Number, a.EnergyForecast ?? 0, percent, "%");
                yield return Report(a, WaterForecast, "water forecast", ValueType.Number, a.WaterForecast ?? 0, percent, "%");
            }
        }

        private static IEnumerable<CommandTemplate> ApplianceCommands(MieleAppliance a, MieleActions? actions, IReadOnlyList<MieleProgram> programs)
        {
            yield return Command(a, StartCommand, "start", ValueType.Boolean, true, ValueSchema.Boolean());
            yield return Command(a, StopCommand, "stop", ValueType.Boolean, true, ValueSchema.Boolean());
            yield return Command(a, PauseCommand, "pause", ValueType.Boolean, true, ValueSchema.Boolean());
            var delay = ValueSchema.Integer("min", null, actions?.StartDelayMin ?? 0, actions?.StartDelayMax);
            delay.Description = "Starts the chosen program after this many minutes";
            yield return Command(a, StartDelay, "start after", ValueType.Number, 60, delay);
            var at = ValueSchema.Text();
            at.Description = "Starts the chosen program at this time, ISO 8601, e.g. a cheap price slot's start";
            yield return Command(a, StartAt, "start at", ValueType.Text, "", at);
            yield return Command(a, Power, "power", ValueType.Boolean, true, ValueSchema.Boolean(ValueSemantics.Switch));
            if (programs.Count > 0)
            {
                var schema = Choices(programs.Select(x => x.Name).ToList());
                schema.Description = "A program, or {\"program\": ..., \"temperature\": °C, \"duration\": minutes}";
                yield return Command(a, Program, "program", ValueType.Text, programs[0].Name, schema);
            }
            if (MieleCodes.IsOven(a.Type) || actions?.TargetTemperatures.Count > 0)
            {
                var range = actions?.TargetTemperatures.FirstOrDefault(x => x.Zone == 1);
                yield return Command(a, TargetTemperature, "set temperature", ValueType.Number, a.TargetTemperature ?? 180,
                    ValueSchema.Number("°C", ValueSemantics.Temperature, range?.Min, range?.Max));
            }
        }

        private static ReportTemplate Report(MieleAppliance a, string key, string name, ValueType type, object? model, ValueSchema schema, string? unit = null)
        {
            var template = new ReportTemplate
            {
                Id = Guid.NewGuid().ToString(),
                Address = Address(a.Id, key),
                Name = $"{a.Name} {name}",
                Type = type,
                Model = model,
                Schema = schema
            };
            if (unit != null)
                template.Parameters["unit"] = unit;
            return template;
        }

        private static CommandTemplate Command(MieleAppliance a, string key, string name, ValueType type, object model, ValueSchema schema) => new()
        {
            Id = Guid.NewGuid().ToString(),
            Address = Address(a.Id, key),
            Name = $"{a.Name} {name}",
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
        /// Declares each appliance's temperature, and its food probe temperature, as Matter temperature sensors on the
        /// RIoT Control Bridge. Core has no washer or oven device types, so nothing else is bridged.
        /// </summary>
        /// <remarks>
        /// The bindings are read against the configuration handed in, because <see cref="GetConfigurationTemplate"/>
        /// mints new template ids on every call. The endpoint ids come from the fabrication number, so an appliance stays
        /// linked to the same Matter endpoints across restarts and re-imports.
        /// </remarks>
        public IEnumerable<MatterEndpointTemplate> GetMatterEndpoints(DeviceConfiguration configuration)
        {
            if (configuration?.ReportTemplates == null)
                yield break;

            foreach (var report in configuration.ReportTemplates)
            {
                var (id, key) = SplitAddress(report.Address);
                if (id == null || key is not (Temperature or CoreTemperature))
                    continue;

                var appliance = FindAppliance(id);
                var core = key == CoreTemperature;
                var endpoint = new MatterEndpointTemplate
                {
                    Id = $"miele:{id}:{(core ? "core" : "temperature")}",
                    Name = appliance != null ? $"{appliance.Name} {(core ? "food probe" : "temperature")}" : report.Name,
                    DeviceType = MatterDeviceType.TemperatureSensor,
                    VendorName = "Miele",
                    ProductName = string.IsNullOrEmpty(appliance?.TechType) ? "Miele appliance" : appliance.TechType
                };
                endpoint.Attributes.Add(new MatterAttributeBinding
                {
                    Attribute = MatterAttribute.TemperatureMeasuredValue,
                    ReportTemplateId = report.Id,
                    Scale = MatterValueScale.CelsiusToHundredths
                });
                yield return endpoint;
            }
        }
    }
}
