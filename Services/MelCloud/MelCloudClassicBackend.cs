using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using RIoT2.Net.Devices.Models;

namespace RIoT2.Net.Devices.Services.MelCloud
{
    /// <summary>
    /// Classic MELCloud (app.melcloud.com), the API of the MELCloud app. It is not documented; the requests follow
    /// pymelcloud. A login returns a context key that every request sends in <c>X-MitsContextKey</c>.
    /// </summary>
    internal sealed class MelCloudClassicBackend : IMelCloudBackend
    {
        internal const string BaseUrl = "https://app.melcloud.com/Mitsubishi.Wifi.Client";
        private const int AirToAir = 0;

        // SetAta's EffectiveFlags: which of the posted settings the unit applies.
        private const int FlagPower = 0x01;
        private const int FlagMode = 0x02;
        private const int FlagTemperature = 0x04;
        private const int FlagFanSpeed = 0x08;
        private const int FlagVaneVertical = 0x10;
        private const int FlagVaneHorizontal = 0x100;

        private static readonly Dictionary<int, string> _modes = new()
        {
            [1] = MelCloudModes.Heat, [2] = MelCloudModes.Dry, [3] = MelCloudModes.Cool, [7] = MelCloudModes.Fan, [8] = MelCloudModes.Auto
        };
        private static readonly Dictionary<int, string> _verticalVanes = new()
        {
            [0] = "auto", [1] = "1", [2] = "2", [3] = "3", [4] = "4", [5] = "5", [7] = "swing"
        };
        private static readonly Dictionary<int, string> _horizontalVanes = new()
        {
            [0] = "auto", [1] = "1", [2] = "2", [3] = "3", [4] = "4", [5] = "5", [8] = "split", [12] = "swing"
        };

        private readonly string _email;
        private readonly string _password;
        private readonly MelCloudSend _send;
        private readonly MelCloudSessionStore _store;
        private readonly ILogger _logger;
        // Device/Get's answer per unit: SetAta posts it back with the changed settings.
        private readonly Dictionary<string, JsonObject> _states = new(StringComparer.Ordinal);
        private readonly HashSet<string> _skipped = new(StringComparer.Ordinal);
        private string? _contextKey;

        public MelCloudClassicBackend(string email, string password, MelCloudSend send, MelCloudSessionStore store, ILogger logger)
        {
            _email = email;
            _password = password;
            _send = send;
            _store = store;
            _logger = logger;
        }

        public async Task<IReadOnlyList<MelCloudUnit>> GetUnitsAsync(CancellationToken cancellationToken)
        {
            var buildings = await SendJsonAsync(HttpMethod.Get, "User/ListDevices", null, cancellationToken).ConfigureAwait(false);
            var units = new List<MelCloudUnit>();
            foreach (var conf in FindDevices(buildings))
            {
                var id = MelCloudJson.Text(conf["DeviceID"]);
                var buildingId = MelCloudJson.Text(conf["BuildingID"]);
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(buildingId))
                    continue;

                var device = conf["Device"] as JsonObject;
                if (MelCloudJson.Number(device?["DeviceType"]) != AirToAir)
                {
                    if (_skipped.Add(id))
                        _logger.LogInformation("MELCloud unit {Unit} is not an air-to-air unit (type {Type}); it is skipped.",
                            id, MelCloudJson.Text(device?["DeviceType"]) ?? "unknown");
                    continue;
                }

                var state = await SendJsonAsync(HttpMethod.Get,
                    $"Device/Get?id={Uri.EscapeDataString(id)}&buildingID={Uri.EscapeDataString(buildingId)}", null, cancellationToken).ConfigureAwait(false) as JsonObject
                    ?? throw new MelCloudUnavailableException($"MELCloud sent no state for unit {id}.");
                _states[id] = state;

                var unit = new MelCloudUnit
                {
                    Id = id,
                    BuildingId = buildingId,
                    Name = Clean(MelCloudJson.Text(conf["DeviceName"])) ?? id
                };
                ReadState(unit, state);
                ReadDevice(unit, device);
                units.Add(unit);
            }
            return units;
        }

        public async Task SetAsync(MelCloudUnit unit, MelCloudChange change, CancellationToken cancellationToken)
        {
            if (!_states.TryGetValue(unit.Id, out var current))
                throw new Core.DeviceNotReadyException($"The state of MELCloud unit {unit.Id} isn't loaded yet.");

            var state = current.DeepClone().AsObject();
            var flags = 0;
            if (change.Power is bool power)
            {
                state["Power"] = power;
                flags |= FlagPower;
            }
            if (change.Mode != null)
            {
                state["OperationMode"] = Key(_modes, change.Mode, "mode");
                flags |= FlagMode;
            }
            if (change.SetTemperature is double temperature)
            {
                state["SetTemperature"] = temperature;
                flags |= FlagTemperature;
            }
            if (change.FanSpeed != null)
            {
                state["SetFanSpeed"] = change.FanSpeed == "auto" ? 0 : int.Parse(change.FanSpeed, CultureInfo.InvariantCulture);
                flags |= FlagFanSpeed;
            }
            if (change.VaneVertical != null)
            {
                state["VaneVertical"] = Key(_verticalVanes, change.VaneVertical, "vertical vane position");
                flags |= FlagVaneVertical;
            }
            if (change.VaneHorizontal != null)
            {
                state["VaneHorizontal"] = Key(_horizontalVanes, change.VaneHorizontal, "horizontal vane position");
                flags |= FlagVaneHorizontal;
            }
            if (flags == 0)
                return;

            state["EffectiveFlags"] = flags;
            state["HasPendingCommand"] = true;
            var answer = await SendJsonAsync(HttpMethod.Post, "Device/SetAta", state, cancellationToken).ConfigureAwait(false);

            // SetAta answers with the unit's state; keep what was sent if it doesn't.
            var updated = answer is JsonObject accepted && accepted["DeviceID"] != null ? accepted : state;
            updated["EffectiveFlags"] = 0;
            _states[unit.Id] = updated;
        }

        private async Task<JsonNode?> SendJsonAsync(HttpMethod method, string path, JsonNode? body, CancellationToken cancellationToken)
        {
            if (_contextKey == null)
            {
                _contextKey = _store.Load()?.ContextKey;
                if (string.IsNullOrEmpty(_contextKey))
                    await LoginAsync(cancellationToken).ConfigureAwait(false);
            }

            for (var attempt = 0; ; attempt++)
            {
                using var request = Request(method, path, body);
                request.Headers.TryAddWithoutValidation("X-MitsContextKey", _contextKey);
                using var response = await MelCloudHttp.SendAsync(_send, request, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    if (attempt > 0)
                        throw new MelCloudAuthenticationException($"MELCloud refused the login for {path} ({(int)response.StatusCode}).");
                    // The context key expired: log in again once.
                    _logger.LogInformation("MELCloud's context key for {Email} expired; logging in again.", MelCloudHttp.Mask(_email));
                    await LoginAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }
                await MelCloudHttp.EnsureSuccessAsync(response, path, cancellationToken).ConfigureAwait(false);
                return await MelCloudHttp.ReadJsonAsync(response, path, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task LoginAsync(CancellationToken cancellationToken)
        {
            _contextKey = null;
            var body = new JsonObject
            {
                ["Email"] = _email,
                ["Password"] = _password,
                ["Language"] = 0,
                ["AppVersion"] = "1.19.1.1",
                ["Persist"] = true,
                ["CaptchaResponse"] = null
            };
            using var request = Request(HttpMethod.Post, "Login/ClientLogin", body);
            using var response = await MelCloudHttp.SendAsync(_send, request, cancellationToken).ConfigureAwait(false);
            await MelCloudHttp.EnsureSuccessAsync(response, "the login", cancellationToken).ConfigureAwait(false);
            var answer = await MelCloudHttp.ReadJsonAsync(response, "the login", cancellationToken).ConfigureAwait(false);

            var key = MelCloudJson.Text(answer?["LoginData"]?["ContextKey"]);
            if (string.IsNullOrEmpty(key))
            {
                var error = MelCloudJson.Text(answer?["ErrorId"]);
                _store.Delete();
                throw new MelCloudAuthenticationException(
                    $"MELCloud refused the login of {MelCloudHttp.Mask(_email)} (error {error ?? "unknown"}); check the e-mail and password.");
            }

            _contextKey = key;
            _store.Save(new MelCloudSession { ContextKey = key });
            _logger.LogInformation("Logged in to MELCloud as {Email}.", MelCloudHttp.Mask(_email));
        }

        private static HttpRequestMessage Request(HttpMethod method, string path, JsonNode? body)
        {
            var request = new HttpRequestMessage(method, $"{BaseUrl}/{path}");
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (X11; Linux x86_64; rv:73.0) Gecko/20100101 Firefox/73.0");
            request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
            request.Headers.TryAddWithoutValidation("Cookie", "policyaccepted=true");
            if (body != null)
                request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            return request;
        }

        /// <summary>
        /// The device entries of ListDevices: a building lists them at its root, in its areas, on its floors and in
        /// the floors' areas. A unit shared into several places is listed once.
        /// </summary>
        internal static IEnumerable<JsonObject> FindDevices(JsonNode? buildings)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var building in MelCloudJson.Objects(buildings))
            {
                var structure = building["Structure"];
                var lists = new List<JsonNode?> { structure?["Devices"] };
                lists.AddRange(MelCloudJson.Objects(structure?["Areas"]).Select(x => x["Devices"]));
                foreach (var floor in MelCloudJson.Objects(structure?["Floors"]))
                {
                    lists.Add(floor["Devices"]);
                    lists.AddRange(MelCloudJson.Objects(floor["Areas"]).Select(x => x["Devices"]));
                }

                foreach (var device in lists.SelectMany(MelCloudJson.Objects))
                {
                    var id = MelCloudJson.Text(device["DeviceID"]);
                    if (id != null && seen.Add(id))
                        yield return device;
                }
            }
        }

        // Reads the unit's capabilities; ReadState has read its mode, which picks the temperature limits.
        private static void ReadDevice(MelCloudUnit unit, JsonObject? device)
        {
            unit.TemperatureStep = MelCloudJson.Number(device?["TemperatureIncrement"]) is double step and > 0 ? step : 0.5;
            var (min, max) = unit.Mode switch
            {
                MelCloudModes.Cool or MelCloudModes.Dry => ("MinTempCoolDry", "MaxTempCoolDry"),
                MelCloudModes.Auto => ("MinTempAutomatic", "MaxTempAutomatic"),
                _ => ("MinTempHeat", "MaxTempHeat")
            };
            unit.MinTemperature = MelCloudJson.Number(device?[min]) ?? 10;
            unit.MaxTemperature = MelCloudJson.Number(device?[max]) ?? 31;
            // The meter reads 0 until its first upload; then it only grows (Wh).
            if (MelCloudJson.Bool(device?["HasEnergyConsumedMeter"]) == true &&
                MelCloudJson.Number(device?["CurrentEnergyConsumed"]) is double energy and > 0)
                unit.EnergyConsumed = Math.Round(energy / 1000, 3);
        }

        private static void ReadState(MelCloudUnit unit, JsonObject state)
        {
            unit.Power = MelCloudJson.Bool(state["Power"]);
            unit.Mode = Name(_modes, state["OperationMode"]);
            unit.SetTemperature = MelCloudJson.Number(state["SetTemperature"]);
            unit.RoomTemperature = MelCloudJson.Number(state["RoomTemperature"]);
            unit.FanSpeed = MelCloudJson.Number(state["SetFanSpeed"]) is double fan
                ? fan == 0 ? "auto" : ((int)fan).ToString(CultureInfo.InvariantCulture)
                : null;
            unit.VaneVertical = Name(_verticalVanes, state["VaneVertical"]);
            unit.VaneHorizontal = Name(_horizontalVanes, state["VaneHorizontal"]);
            unit.Offline = MelCloudJson.Bool(state["Offline"]);
        }

        private static string? Name(Dictionary<int, string> names, JsonNode? node) =>
            MelCloudJson.Number(node) is double number && names.TryGetValue((int)number, out var name) ? name : null;

        private static int Key(Dictionary<int, string> names, string name, string what)
        {
            foreach (var (key, value) in names)
            {
                if (value == name)
                    return key;
            }
            throw new ArgumentException($"Classic MELCloud has no {what} '{name}'.");
        }

        // A unit name is chosen in the MELCloud app and ends up in logs and template names.
        internal static string? Clean(string? name) =>
            string.IsNullOrWhiteSpace(name) ? null : new string(name.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
    }
}
