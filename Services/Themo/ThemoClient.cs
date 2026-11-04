using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using RIoT2.Net.Devices.Models;

namespace RIoT2.Net.Devices.Services.Themo
{
    /// <summary>Sends one HTTP request. The device's <c>SendAsync</c>, which the tests replace.</summary>
    internal delegate Task<HttpResponseMessage> ThemoSend(HttpRequestMessage request, CancellationToken cancellationToken);

    /// <summary>Themo refused the e-mail or the password; retrying doesn't help.</summary>
    internal sealed class ThemoAuthenticationException(string message) : Exception(message);

    /// <summary>Themo didn't answer, limited the requests (429) or failed (5xx); a later poll may succeed.</summary>
    internal sealed class ThemoUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

    /// <summary>Themo refused a request (400, 403, 404); retrying doesn't help.</summary>
    internal sealed class ThemoRefusedException(HttpStatusCode status, string message) : Exception(message)
    {
        public HttpStatusCode Status { get; } = status;
    }

    internal sealed record ThemoEnvironment(int Id, string Name);

    /// <summary>
    /// The Themo Public API (connect.themo.io) with the Themo app's account. Every call asks for API version 2.1,
    /// the last one with the hourly device data. The login's token is kept in memory and replaced after a 401. Not
    /// thread-safe: the device sends one call at a time.
    /// </summary>
    internal sealed class ThemoClient
    {
        internal const string ApiUrl = "https://connect.themo.io";
        internal const string ApiVersion = "2.1";

        private readonly string _email;
        private readonly string _password;
        private readonly ThemoSend _send;
        private readonly ILogger _logger;
        private string? _token;
        private bool _seriesLogged;

        public ThemoClient(string email, string password, ThemoSend send, ILogger logger)
        {
            _email = email;
            _password = password;
            _send = send;
            _logger = logger;
        }

        public async Task<IReadOnlyList<ThemoEnvironment>> GetEnvironmentsAsync(CancellationToken cancellationToken)
        {
            var json = await ApiAsync(HttpMethod.Get, "api/environments", null, "the environments", cancellationToken).ConfigureAwait(false);
            return Objects(json)
                .Select(x => new ThemoEnvironment(Int(x["Id"]) ?? 0, Text(x["Name"]) ?? ""))
                .Where(x => x.Id != 0).ToList();
        }

        /// <summary>The environment's thermostats with their state.</summary>
        public async Task<IReadOnlyList<ThemoThermostat>> GetThermostatsAsync(int environmentId, CancellationToken cancellationToken)
        {
            var json = await ApiAsync(HttpMethod.Get, $"api/environments/{environmentId}/devices?state=true", null, "the thermostats", cancellationToken)
                .ConfigureAwait(false);
            return Objects(json).Select(x => ReadThermostat(environmentId, x)).Where(x => x.Id != 0).ToList();
        }

        /// <summary>The thermostat's minimum temperature schedules (the ones the app lets you pick).</summary>
        public async Task<IReadOnlyList<ThemoSchedule>> GetSchedulesAsync(int environmentId, int thermostatId, CancellationToken cancellationToken)
        {
            var json = await ApiAsync(HttpMethod.Get, $"api/environments/{environmentId}/devices/{thermostatId}/schedules", null, "the schedules",
                cancellationToken).ConfigureAwait(false);
            return Objects(json)
                .Where(x => string.Equals(Text(x["Parameter"]), "MinTemperature", StringComparison.OrdinalIgnoreCase))
                .Select(x => new ThemoSchedule(Int(x["Id"]) ?? 0, Text(x["Name"])?.Trim() ?? "", Bool(x["Active"]) ?? false))
                .Where(x => x.Id != 0 && x.Name.Length > 0).ToList();
        }

        /// <summary>Activates <paramref name="schedule"/>; Themo deactivates the one that was active.</summary>
        public Task ActivateScheduleAsync(int environmentId, int thermostatId, ThemoSchedule schedule, CancellationToken cancellationToken) =>
            ApiAsync(HttpMethod.Put, $"api/environments/{environmentId}/devices/{thermostatId}/schedules/{schedule.Id}",
                JsonSerializer.Serialize(new { Name = schedule.Name, Active = true }), "the schedule change", cancellationToken);

        /// <summary>The hourly consumption (kWh) of the finished hours from <paramref name="from"/> to <paramref name="to"/>.</summary>
        public async Task<List<ThemoEnergyHour>> GetEnergyAsync(int environmentId, int thermostatId, DateTimeOffset from, DateTimeOffset to,
            CancellationToken cancellationToken)
        {
            var path = $"api/environments/{environmentId}/devices/{thermostatId}/data" +
                $"?from={Uri.EscapeDataString(Utc(from))}&to={Uri.EscapeDataString(Utc(to))}";
            var json = await ApiAsync(HttpMethod.Get, path, null, "the hourly data", cancellationToken).ConfigureAwait(false);
            if (!_seriesLogged && _logger.IsEnabled(LogLevel.Debug))
            {
                _seriesLogged = true;
                foreach (var series in Objects(json))
                    _logger.LogDebug("Themo hourly data series {Series}: {Columns}", Text(series["Name"]),
                        string.Join(", ", Objects(series["TimeSeriesDefinitions"]).Select(x => $"{Text(x["Name"])} ({Text(x["UnitName"])})")));
            }
            return ThemoEnergy.Parse(json);
        }

        /// <summary>Sends one command to the thermostat, e.g. <c>{"CMT": 21.5}</c>; the value is a string, int or double.</summary>
        public async Task SendCommandAsync(int environmentId, int thermostatId, string field, object value, CancellationToken cancellationToken)
        {
            JsonNode node = value switch
            {
                string text => JsonValue.Create(text),
                int number => JsonValue.Create(number),
                double number => JsonValue.Create(number),
                _ => throw new ArgumentException($"A Themo command value is text or a number, not {value.GetType().Name}.", nameof(value))
            };
            var body = new JsonObject { [field] = node }.ToJsonString();
            var json = await ApiAsync(HttpMethod.Post, $"api/environments/{environmentId}/devices/{thermostatId}/commands/message", body,
                "the command", cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("Themo took command {Field}: {Status}", field, Text(json?["Status"]) ?? "sent");
        }

        internal static ThemoThermostat ReadThermostat(int environmentId, JsonObject json)
        {
            var state = json["State"] as JsonObject;
            var parameters = state?["DeviceParameters"] as JsonObject;
            var floor = Bool(parameters?["IsFloor"]) == true || Bool(parameters?["IsCombi"]) == true;
            var thermostat = new ThemoThermostat
            {
                Id = Int(json["Id"]) ?? 0,
                EnvironmentId = Int(json["EnvironmentId"]) ?? environmentId,
                Name = Text(json["Name"])?.Trim() ?? "",
                Model = Model(Text(json["Tags"])),
                Software = Text(state?["SW"]) ?? Text(json["SW"]),
                Schedule = Text(json["TemperatureSchedule"])?.Trim()
            };
            if (thermostat.Name.Length == 0)
                thermostat.Name = $"Themo {thermostat.Id}";
            if (state == null)
                return thermostat;

            thermostat.Temperature = ThemoEnergy.Number(state["Info"]);
            thermostat.RoomTemperature = ThemoEnergy.Number(state["RT"]);
            thermostat.FloorTemperature = floor ? ThemoEnergy.Number(state["FloorT"]) : null;
            thermostat.OutsideTemperature = ThemoEnergy.Number(state["OT"]);
            thermostat.SetTemperature = ThemoEnergy.Number(state["MT"]);
            thermostat.Mode = ThemoModes.FromApi(Text(state["Mode"]));
            thermostat.Heating = Int(state["LS"]) is int load ? load != 0 : null;
            thermostat.Connected = Bool(state["CS"]);
            thermostat.Lights = Int(state["Lights"]) is int lights ? lights != 0 : null;
            thermostat.Lock = Int(state["Lock"]);
            thermostat.FrostProtection = Int(state["FTS"]) is int frost ? frost != 0 : null;
            thermostat.FrostTemperature = ThemoEnergy.Number(state["FT"]);
            thermostat.MaxPower = ThemoEnergy.MaxPowerKw(ThemoEnergy.Number(state["MP"]));
            return thermostat;
        }

        // The model token in the pipe-separated tags, e.g. "T700".
        private static string Model(string? tags) =>
            tags?.Split('|').Select(x => x.Trim()).FirstOrDefault(x => x.Length > 1 && x[0] == 'T' && x[1..].All(char.IsAsciiDigit)) ?? "Smart Thermostat";

        private static string Utc(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

        private async Task<JsonNode?> ApiAsync(HttpMethod method, string path, string? body, string what, CancellationToken cancellationToken)
        {
            for (var attempt = 0; ; attempt++)
            {
                var token = _token ?? await LoginAsync(cancellationToken).ConfigureAwait(false);
                using var request = new HttpRequestMessage(method, Url(path));
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
                request.Headers.TryAddWithoutValidation("Accept", "application/json");
                if (body != null)
                    request.Content = new StringContent(body, Encoding.UTF8, "application/json");

                using var response = await SendAsync(request, what, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    _token = null;
                    if (attempt == 0)
                        continue;
                    throw new ThemoAuthenticationException("Themo accepted the login but refuses its token.");
                }
                await EnsureSuccessAsync(response, what, cancellationToken).ConfigureAwait(false);
                return await ReadJsonAsync(response, what, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task<string> LoginAsync(CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Url("api/auth/login"))
            {
                Content = new StringContent(JsonSerializer.Serialize(new { Username = _email, Password = _password }), Encoding.UTF8, "application/json")
            };
            using var response = await SendAsync(request, "the login", cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
                throw new ThemoAuthenticationException($"Themo refused the login of {Mask(_email)}; check the e-mail and password.");
            await EnsureSuccessAsync(response, "the login", cancellationToken).ConfigureAwait(false);

            var json = await ReadJsonAsync(response, "the login", cancellationToken).ConfigureAwait(false);
            var token = Text(json?["Token"]);
            if (string.IsNullOrEmpty(token))
            {
                var message = Text(json?["Message"]);
                throw new ThemoAuthenticationException(
                    $"Themo refused the login of {Mask(_email)}{(string.IsNullOrEmpty(message) ? "" : $" ({message})")}; check the e-mail and password.");
            }
            _token = token;
            return token;
        }

        private static string Url(string path) => $"{ApiUrl}/{path}{(path.Contains('?') ? '&' : '?')}api-version={ApiVersion}";

        private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, string what, CancellationToken cancellationToken)
        {
            try
            {
                return await _send(request, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException error)
            {
                throw new ThemoUnavailableException($"Could not reach Themo for {what}: {error.Message}", error);
            }
            catch (TimeoutException error)
            {
                throw new ThemoUnavailableException($"Themo did not answer for {what}.", error);
            }
            catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ThemoUnavailableException($"Themo did not answer in time for {what}.", error);
            }
        }

        private static async Task EnsureSuccessAsync(HttpResponseMessage response, string what, CancellationToken cancellationToken)
        {
            if (response.IsSuccessStatusCode)
                return;
            var status = (int)response.StatusCode;
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new ThemoUnavailableException($"Themo is limiting requests (429) for {what}; the next poll tries again.");
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            // Themo's errors are {"Message": ..., "Code": ...}.
            string? message = null;
            try
            {
                message = Text(JsonNode.Parse(body)?["Message"]);
            }
            catch (JsonException)
            {
            }
            message ??= body.Length > 200 ? body[..200] : body;
            var text = $"Themo answered {status} {response.ReasonPhrase} for {what}. {message}".TrimEnd();
            if (status >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout)
                throw new ThemoUnavailableException(text);
            throw new ThemoRefusedException(response.StatusCode, text);
        }

        private static async Task<JsonNode?> ReadJsonAsync(HttpResponseMessage response, string what, CancellationToken cancellationToken)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text))
                return null;
            try
            {
                return JsonNode.Parse(text);
            }
            catch (JsonException error)
            {
                throw new ThemoUnavailableException($"Themo's answer for {what} is not JSON.", error);
            }
        }

        /// <summary>The e-mail with only its first letters, for logs and errors.</summary>
        internal static string Mask(string email)
        {
            var at = email.IndexOf('@');
            return at <= 0 ? "***" : $"{email[0]}***@{(at + 1 < email.Length ? email[at + 1] : '*')}***";
        }

        private static IEnumerable<JsonObject> Objects(JsonNode? node) => node is JsonArray array ? array.OfType<JsonObject>() : [];

        private static int? Int(JsonNode? node) => ThemoEnergy.Number(node) is double number ? (int)Math.Round(number) : null;

        private static bool? Bool(JsonNode? node)
        {
            if (node is not JsonValue value)
                return null;
            if (value.TryGetValue<bool>(out var flag))
                return flag;
            if (value.TryGetValue<string>(out var text) && bool.TryParse(text, out flag))
                return flag;
            return ThemoEnergy.Number(node) is double number ? number != 0 : null;
        }

        private static string? Text(JsonNode? node)
        {
            if (node is not JsonValue value)
                return null;
            if (value.TryGetValue<string>(out var text))
                return text;
            return ThemoEnergy.Number(node)?.ToString(CultureInfo.InvariantCulture);
        }
    }
}
