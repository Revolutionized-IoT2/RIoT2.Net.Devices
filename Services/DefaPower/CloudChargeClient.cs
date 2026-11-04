using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using RIoT2.Net.Devices.Models;

namespace RIoT2.Net.Devices.Services.DefaPower
{
    /// <summary>Sends one HTTP request. The device's <c>SendAsync</c>, which the tests replace.</summary>
    internal delegate Task<HttpResponseMessage> CloudChargeSend(HttpRequestMessage request, CancellationToken cancellationToken);

    /// <summary>CloudCharge refused the login token: the device has to log in again with an SMS code.</summary>
    internal sealed class CloudChargeAuthenticationException(string message) : Exception(message);

    /// <summary>CloudCharge didn't answer, limited the requests (429) or failed (5xx); a later poll may succeed.</summary>
    internal sealed class CloudChargeUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

    /// <summary>CloudCharge refused a request (400, 403, 404, ...): a wrong SMS code, for example. Retrying doesn't help.</summary>
    internal sealed class CloudChargeRefusedException(HttpStatusCode status, string message) : Exception(message)
    {
        public HttpStatusCode Status { get; } = status;
    }

    /// <summary>
    /// The CloudCharge API (prod.cloudcharge.se) that the DEFA Power and CloudCharge apps use. It isn't public; this
    /// follows the ha-defa-power Home Assistant integration. A login is a phone number and an SMS code; the login's user
    /// id and token go in the <c>x-user</c> and <c>x-authorization</c> headers. The API runs on several servers and a
    /// cookie picks one: the SMS code is only known to the server that sent it, so the client keeps the cookies itself.
    /// The API limits requests per token, so the client waits <see cref="RequestInterval"/> between requests. Not
    /// thread-safe beyond that: the device sends one call sequence at a time.
    /// </summary>
    internal sealed class CloudChargeClient
    {
        internal const string ApiUrl = "https://prod.cloudcharge.se/services/user";

        // The CloudCharge app's public app id (the API's "devToken"), the one ha-defa-power uses too. The API allows one
        // login per app, so logging in as the CloudCharge app leaves the DEFA Power app on the phone logged in.
        internal const string AppId = "X5zVn6MCWvrf6ft2";

        private static readonly string[] _countryCodesWithoutTrunkZero = ["45", "46", "47", "354", "358"];

        private readonly CloudChargeSend _send;
        private readonly ILogger _logger;
        private readonly TimeProvider _time;
        private readonly SemaphoreSlim _throttle = new(1, 1);
        private readonly Dictionary<string, string> _cookies = new(StringComparer.Ordinal);
        private DateTimeOffset _lastRequest = DateTimeOffset.MinValue;

        public CloudChargeClient(CloudChargeSend send, ILogger logger, TimeProvider time)
        {
            _send = send;
            _logger = logger;
            _time = time;
        }

        /// <summary>The shortest time between two requests; CloudCharge limits requests per token.</summary>
        public TimeSpan RequestInterval { get; set; } = TimeSpan.FromSeconds(1);

        public string? UserId { get; private set; }

        public string? Token { get; private set; }

        public bool LoggedIn => !string.IsNullOrEmpty(UserId) && !string.IsNullOrEmpty(Token);

        public void SetLogin(string userId, string token)
        {
            UserId = userId;
            Token = token;
        }

        public void ForgetLogin()
        {
            UserId = null;
            Token = null;
        }

        /// <summary>
        /// The phone number as the API takes it: the country code and the national number, digits only. Spaces, dashes and
        /// a leading + or 00 are dropped. A trunk zero after a Nordic country code is refused: with it the SMS arrives, but
        /// the login with its code fails.
        /// </summary>
        public static string NormalizePhoneNumber(string phoneNumber)
        {
            var text = (phoneNumber ?? "").Trim();
            var digits = new string(text.Where(c => c is not (' ' or '-' or '(' or ')' or '.' or '+')).ToArray());
            if (!text.StartsWith('+') && digits.StartsWith("00", StringComparison.Ordinal))
                digits = digits[2..];
            if (digits.Length < 7 || digits.Length > 15 || !digits.All(char.IsAsciiDigit))
                throw new ArgumentException("The phone number must be in international format, e.g. +358401234567.", nameof(phoneNumber));
            if (_countryCodesWithoutTrunkZero.FirstOrDefault(x => digits.StartsWith(x + "0", StringComparison.Ordinal)) is string code)
                throw new ArgumentException($"Leave out the leading zero of the national number: +{code} {digits[(code.Length + 1)..]}, not +{code} 0...",
                    nameof(phoneNumber));
            return digits;
        }

        /// <summary>The phone number with only its first and last digits, for logs and errors.</summary>
        internal static string Mask(string phoneNumber) =>
            phoneNumber.Length <= 5 ? "***" : $"+{phoneNumber[..3]}***{phoneNumber[^2..]}";

        /// <summary>Asks CloudCharge to send a login code to <paramref name="phoneNumber"/> by SMS.</summary>
        public async Task SendSmsCodeAsync(string phoneNumber, CancellationToken cancellationToken)
        {
            var body = new JsonObject { ["phoneNr"] = phoneNumber, ["devToken"] = AppId };
            await ApiAsync(HttpMethod.Post, "prelogin", body, "the SMS code", authenticated: false, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("CloudCharge sent a login code to {Phone}.", Mask(phoneNumber));
        }

        /// <summary>Logs in with the SMS code; the device keeps the login it returns.</summary>
        public async Task<DefaPowerLogin> LoginAsync(string phoneNumber, string code, CancellationToken cancellationToken)
        {
            var body = new JsonObject { ["phoneNr"] = phoneNumber, ["password"] = code.Trim(), ["devToken"] = AppId };
            var json = await ApiAsync(HttpMethod.Post, "login", body, "the login", authenticated: false, cancellationToken).ConfigureAwait(false);
            var userId = Text(json?["id"]);
            var token = Text(json?["token"]);
            if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(token))
                throw new CloudChargeRefusedException(HttpStatusCode.OK, "CloudCharge accepted the login but returned no token.");
            SetLogin(userId, token);
            return new DefaPowerLogin { UserId = userId, Token = token };
        }

        /// <summary>Checks the login: reads the profile.</summary>
        public Task ReadProfileAsync(CancellationToken cancellationToken) =>
            ApiAsync(HttpMethod.Get, "profile", null, "the profile", authenticated: true, cancellationToken);

        /// <summary>The ids of the account's own charge points and of the ones shared with it.</summary>
        public async Task<IReadOnlyList<string>> GetChargePointIdsAsync(CancellationToken cancellationToken)
        {
            var ids = new List<string>();
            var own = await ApiAsync(HttpMethod.Get, "chargers/private", null, "the chargers", authenticated: true, cancellationToken)
                .ConfigureAwait(false);
            ids.AddRange(Objects(own).Select(x => Text(x["data"]?["id"])).OfType<string>());

            try
            {
                var mine = await ApiAsync(HttpMethod.Get, "mychargers", null, "the shared chargers", authenticated: true, cancellationToken)
                    .ConfigureAwait(false);
                ids.AddRange(Objects(mine?["receivingAccess"]).Select(x => Text(x["chargePoint"]?["id"])).OfType<string>());
            }
            catch (CloudChargeRefusedException error)
            {
                _logger.LogDebug("CloudCharge refused the shared chargers: {Reason}", error.Message);
            }
            return ids.Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        }

        /// <summary>The charge point's connectors with their settings.</summary>
        public async Task<IReadOnlyList<DefaPowerConnector>> GetConnectorsAsync(string chargePointId, CancellationToken cancellationToken)
        {
            var json = await ApiAsync(HttpMethod.Post, "chargepoints/get", new JsonObject { ["token"] = chargePointId }, "the charger",
                authenticated: true, cancellationToken).ConfigureAwait(false);
            return json is JsonObject chargePoint ? ReadConnectors(chargePointId, chargePoint) : [];
        }

        public async Task ReadOperationalDataAsync(DefaPowerConnector connector, CancellationToken cancellationToken)
        {
            var json = await ApiAsync(HttpMethod.Get, $"connector/{Escape(connector.Id)}/operationaldata", null, "the charger state",
                authenticated: true, cancellationToken).ConfigureAwait(false);
            if (json is JsonObject data)
                ApplyOperationalData(connector, data);
        }

        /// <summary>Asks the charger to send its meter more often while it charges.</summary>
        public Task StartLiveConsumptionAsync(string connectorId, CancellationToken cancellationToken) =>
            ApiAsync(HttpMethod.Post, $"connector/{Escape(connectorId)}/startliveconsumption", null, "the live consumption",
                authenticated: true, cancellationToken);

        /// <summary>The current limits the charger accepts, in amperes, smallest first.</summary>
        public async Task<List<int>> GetCurrentAlternativesAsync(string connectorId, CancellationToken cancellationToken)
        {
            var json = await ApiAsync(HttpMethod.Get, $"connector/{Escape(connectorId)}/maxcurrent/alternatives", null, "the current limits",
                authenticated: true, cancellationToken).ConfigureAwait(false);
            var keys = json switch
            {
                JsonObject map => map.Select(x => x.Key),
                JsonArray list => list.Select(Text).OfType<string>(),
                _ => []
            };
            return keys
                .Select(x => double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? (int?)Math.Round(number) : null)
                .OfType<int>().Distinct().Order().ToList();
        }

        public Task SetCurrentLimitAsync(string connectorId, int current, CancellationToken cancellationToken) =>
            ApiAsync(HttpMethod.Post, $"connector/{Escape(connectorId)}/maxcurrent?current={current.ToString(CultureInfo.InvariantCulture)}", null,
                "the current limit", authenticated: true, cancellationToken);

        public Task StartChargingAsync(string alias, CancellationToken cancellationToken) =>
            ApiAsync(HttpMethod.Post, "charging/start", new JsonObject { ["alias"] = alias }, "starting the charging", authenticated: true, cancellationToken);

        public Task StopChargingAsync(string alias, CancellationToken cancellationToken) =>
            ApiAsync(HttpMethod.Post, "charging/stop", new JsonObject { ["alias"] = alias }, "stopping the charging", authenticated: true, cancellationToken);

        /// <summary>Restarts the charger; <paramref name="type"/> is <c>soft</c> or <c>hard</c>.</summary>
        public Task RestartAsync(string connectorId, string type, CancellationToken cancellationToken) =>
            ApiAsync(HttpMethod.Post, $"connector/{Escape(connectorId)}/reset?type={type}", null, "the restart", authenticated: true, cancellationToken);

        public async Task ReadEcoModeAsync(DefaPowerConnector connector, CancellationToken cancellationToken)
        {
            if (await GetEcoModeAsync(connector.Id, cancellationToken).ConfigureAwait(false) is JsonObject eco)
                ApplyEcoMode(connector, eco);
        }

        /// <summary>
        /// Changes eco mode's <paramref name="active"/> flag or <paramref name="hoursToCharge"/>: reads the configuration
        /// and writes it back changed, so the other settings (the pickup times) stay.
        /// </summary>
        public async Task UpdateEcoModeAsync(DefaPowerConnector connector, bool? active, int? hoursToCharge, CancellationToken cancellationToken)
        {
            var eco = await GetEcoModeAsync(connector.Id, cancellationToken).ConfigureAwait(false) as JsonObject
                ?? throw new CloudChargeRefusedException(HttpStatusCode.NotFound, "CloudCharge has no eco mode settings for the charger.");
            var request = new JsonObject
            {
                ["active"] = active ?? Bool(eco["active"]) ?? false,
                ["pickupTimeEnabled"] = Bool(eco["pickupTimeEnabled"]) ?? false,
                ["hoursToCharge"] = hoursToCharge ?? Int(eco["hoursToCharge"]) ?? 4,
                ["dayOfWeekMap"] = eco["dayOfWeekMap"]?.DeepClone() ?? new JsonObject()
            };
            await ApiAsync(HttpMethod.Put, $"connector/{Escape(connector.Id)}/ecomode/configuration", request, "the eco mode",
                authenticated: true, cancellationToken).ConfigureAwait(false);
            ApplyEcoMode(connector, request);
        }

        private Task<JsonNode?> GetEcoModeAsync(string connectorId, CancellationToken cancellationToken) =>
            ApiAsync(HttpMethod.Get, $"connector/{Escape(connectorId)}/ecomode/configuration", null, "the eco mode", authenticated: true, cancellationToken);

        public async Task ReadManualSchedulesAsync(DefaPowerConnector connector, CancellationToken cancellationToken)
        {
            var json = await ApiAsync(HttpMethod.Get, $"connector/{Escape(connector.Id)}/manual-schedules", null, "the manual schedules",
                authenticated: true, cancellationToken).ConfigureAwait(false);
            if (json is JsonObject schedules)
                connector.ManualSchedules = Bool(schedules["schedulingEnabled"]) ?? connector.ManualSchedules;
        }

        public Task SetManualSchedulesAsync(string connectorId, bool enabled, CancellationToken cancellationToken) =>
            ApiAsync(HttpMethod.Put, $"connector/{Escape(connectorId)}/manual-schedules/enable/{(enabled ? "true" : "false")}", null,
                "the manual schedules", authenticated: true, cancellationToken);

        public async Task ReadActiveScheduleAsync(DefaPowerConnector connector, CancellationToken cancellationToken)
        {
            var json = await ApiAsync(HttpMethod.Get, $"connector/{Escape(connector.Id)}/schedule/active-settings", null, "the active schedule",
                authenticated: true, cancellationToken).ConfigureAwait(false);
            if (json is JsonObject settings)
                ApplyActiveSchedule(connector, settings);
        }

        /// <summary>Charges now: overrides the eco mode or manual schedule that pauses the charging.</summary>
        public async Task OverrideScheduleAsync(DefaPowerConnector connector, CancellationToken cancellationToken)
        {
            var json = await ApiAsync(HttpMethod.Put, $"connector/{Escape(connector.Id)}/schedule/override", null, "charging now",
                authenticated: true, cancellationToken).ConfigureAwait(false);
            if (json is JsonObject settings)
                ApplyActiveSchedule(connector, settings);
        }

        internal static List<DefaPowerConnector> ReadConnectors(string chargePointId, JsonObject chargePoint)
        {
            var aliases = chargePoint["aliasMap"] as JsonObject;
            if (aliases == null)
                return [];
            var chargePointName = Text(chargePoint["nickname"])?.Trim() is { Length: > 0 } nickname ? nickname : Text(chargePoint["displayName"])?.Trim();
            var connectors = new List<DefaPowerConnector>();
            foreach (var (alias, node) in aliases)
            {
                if (node is not JsonObject json || Text(json["id"]) is not { Length: > 0 } id)
                    continue;
                var capabilities = json["capabilities"] as JsonObject;
                var name = new[] { Text(json["nickname"]), Text(json["displayName"]), chargePointName, alias }
                    .Select(x => x?.Trim()).FirstOrDefault(x => !string.IsNullOrEmpty(x)) ?? id;
                var connector = new DefaPowerConnector
                {
                    Id = id,
                    Alias = alias,
                    ChargePointId = chargePointId,
                    Name = name,
                    Vendor = Text(json["vendor"]),
                    Model = Text(json["model"]),
                    Firmware = Text(json["firmwareVersion"]),
                    EcoModeCapable = Bool(capabilities?["ecoMode"]) ?? false,
                    ManualSchedulesCapable = Bool(capabilities?["manualSchedules"]) ?? false,
                    CurrentLimit = Int(json["maxProfileCurrent"]),
                    Status = DefaPowerStates.CamelCase(Text(json["status"])),
                    Meter = Number(json["meterValue"]),
                    Power = Number(json["power"]),
                    Online = Bool(json["hbTimeout"]) is bool timeout ? !timeout : null,
                    ErrorCode = ErrorCode(Text(json["errorCode"]))
                };
                connectors.Add(connector);
            }
            // A charge point with more than one connector: tell them apart by the connector number.
            if (connectors.Count > 1)
            {
                foreach (var connector in connectors)
                {
                    var number = Int(aliases[connector.Alias]?["connector"]);
                    if (number != null && connectors.Count(x => x.Name == connector.Name) > 1)
                        connector.Name = $"{connector.Name} #{number.Value.ToString(CultureInfo.InvariantCulture)}";
                }
            }
            return connectors;
        }

        internal static void ApplyOperationalData(DefaPowerConnector connector, JsonObject data)
        {
            var ocpp = data["ocpp"] as JsonObject;
            connector.Status = DefaPowerStates.CamelCase(Text(ocpp?["status"])) ?? connector.Status;
            connector.ChargingState = DefaPowerStates.CamelCase(Text(ocpp?["chargingState"])) ?? connector.ChargingState;
            connector.Meter = Number(data["meterValue"]) ?? connector.Meter;
            connector.TransactionEnergy = Number(data["transactionMeterValue"]);
            connector.Power = Number(data["powerConsumption"]) ?? connector.Power;
            if (Bool(data["hbTimeout"]) is bool timeout)
                connector.Online = !timeout;
            if (data.ContainsKey("errorCode"))
                connector.ErrorCode = ErrorCode(Text(data["errorCode"]));
        }

        internal static void ApplyEcoMode(DefaPowerConnector connector, JsonObject eco)
        {
            connector.EcoMode = Bool(eco["active"]) ?? connector.EcoMode;
            connector.EcoHoursToCharge = Int(eco["hoursToCharge"]) ?? connector.EcoHoursToCharge;
            if (Bool(eco["scheduleOverridden"]) is bool overridden)
                connector.ScheduleOverridden = overridden;
        }

        internal static void ApplyActiveSchedule(DefaPowerConnector connector, JsonObject settings)
        {
            connector.PausedBy = Text(settings["pausedBy"])?.Trim().ToLowerInvariant() switch
            {
                null or "" => DefaPowerStates.PausedByNone,
                "ecomode" or "eco_mode" => DefaPowerStates.PausedByEcoMode,
                "manual_schedules" or "manualschedules" => DefaPowerStates.PausedByManualSchedules,
                var other => DefaPowerStates.CamelCase(other)
            };
            connector.PausedUntil = Time(Text(settings["pausedUntil"]));
            if (Bool(settings["overrideSmartCharging"]) is bool overridden)
                connector.ScheduleOverridden = overridden;
        }

        // "NoError" is OCPP's no error; report it as empty.
        private static string ErrorCode(string? code) =>
            string.IsNullOrWhiteSpace(code) || code.Equals("NoError", StringComparison.OrdinalIgnoreCase) ? "" : code.Trim();

        // A time without an offset is UTC.
        private static DateTimeOffset? Time(string? text) =>
            !string.IsNullOrWhiteSpace(text) && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time) ? time : null;

        private static string Escape(string id) => Uri.EscapeDataString(id);

        private async Task<JsonNode?> ApiAsync(HttpMethod method, string path, JsonNode? body, string what, bool authenticated,
            CancellationToken cancellationToken)
        {
            if (authenticated && !LoggedIn)
                throw new CloudChargeAuthenticationException("The device isn't logged in to CloudCharge.");

            using var request = new HttpRequestMessage(method, $"{ApiUrl}/{path}");
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            if (authenticated)
            {
                request.Headers.TryAddWithoutValidation("x-authorization", Token);
                request.Headers.TryAddWithoutValidation("x-user", UserId);
            }
            if (_cookies.Count > 0)
                request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", _cookies.Select(x => $"{x.Key}={x.Value}")));
            if (body != null)
                request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

            using var response = await SendAsync(request, what, cancellationToken).ConfigureAwait(false);
            KeepCookies(response);
            if (response.StatusCode == HttpStatusCode.Unauthorized && authenticated)
                throw new CloudChargeAuthenticationException("CloudCharge refused the login; log in again with an SMS code.");
            await EnsureSuccessAsync(response, what, cancellationToken).ConfigureAwait(false);
            return await ReadJsonAsync(response, what, cancellationToken).ConfigureAwait(false);
        }

        private void KeepCookies(HttpResponseMessage response)
        {
            if (!response.Headers.TryGetValues("Set-Cookie", out var cookies))
                return;
            foreach (var cookie in cookies)
            {
                var pair = cookie.Split(';', 2)[0];
                var equals = pair.IndexOf('=', StringComparison.Ordinal);
                if (equals > 0)
                    _cookies[pair[..equals].Trim()] = pair[(equals + 1)..].Trim();
            }
        }

        private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, string what, CancellationToken cancellationToken)
        {
            await _throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var wait = _lastRequest + RequestInterval - _time.GetUtcNow();
                if (wait > TimeSpan.Zero)
                    await Task.Delay(wait, _time, cancellationToken).ConfigureAwait(false);
                _lastRequest = _time.GetUtcNow();
                return await _send(request, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException error)
            {
                throw new CloudChargeUnavailableException($"Could not reach CloudCharge for {what}: {error.Message}", error);
            }
            catch (TimeoutException error)
            {
                throw new CloudChargeUnavailableException($"CloudCharge did not answer for {what}.", error);
            }
            catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
            {
                throw new CloudChargeUnavailableException($"CloudCharge did not answer in time for {what}.", error);
            }
            finally
            {
                _throttle.Release();
            }
        }

        private static async Task EnsureSuccessAsync(HttpResponseMessage response, string what, CancellationToken cancellationToken)
        {
            if (response.IsSuccessStatusCode)
                return;
            var status = (int)response.StatusCode;
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new CloudChargeUnavailableException($"CloudCharge is limiting requests (429) for {what}; the next poll tries again.");
            var body = response.Content == null ? "" : await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            string? message = null;
            try
            {
                var json = JsonNode.Parse(body);
                message = Text(json?["message"]) ?? Text(json?["error"]) ?? Text(json?["Message"]);
            }
            catch (JsonException)
            {
            }
            message ??= body.Length > 200 ? body[..200] : body;
            var text = $"CloudCharge answered {status} {response.ReasonPhrase} for {what}. {message}".TrimEnd();
            if (status >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout)
                throw new CloudChargeUnavailableException(text);
            throw new CloudChargeRefusedException(response.StatusCode, text);
        }

        private static async Task<JsonNode?> ReadJsonAsync(HttpResponseMessage response, string what, CancellationToken cancellationToken)
        {
            var text = response.Content == null ? "" : await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text))
                return null;
            try
            {
                return JsonNode.Parse(text);
            }
            catch (JsonException error)
            {
                throw new CloudChargeUnavailableException($"CloudCharge's answer for {what} is not JSON.", error);
            }
        }

        private static IEnumerable<JsonObject> Objects(JsonNode? node) => node is JsonArray array ? array.OfType<JsonObject>() : [];

        internal static double? Number(JsonNode? node)
        {
            if (node is not JsonValue value)
                return null;
            if (value.TryGetValue<double>(out var number))
                return number;
            if (value.TryGetValue<string>(out var text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                return number;
            return null;
        }

        private static int? Int(JsonNode? node) => Number(node) is double number ? (int)Math.Round(number) : null;

        private static bool? Bool(JsonNode? node)
        {
            if (node is not JsonValue value)
                return null;
            if (value.TryGetValue<bool>(out var flag))
                return flag;
            if (value.TryGetValue<string>(out var text) && bool.TryParse(text, out flag))
                return flag;
            return Number(node) is double number ? number != 0 : null;
        }

        private static string? Text(JsonNode? node)
        {
            if (node is not JsonValue value)
                return null;
            if (value.TryGetValue<string>(out var text))
                return text;
            return Number(node) is double number ? number.ToString(CultureInfo.InvariantCulture) : null;
        }
    }
}
