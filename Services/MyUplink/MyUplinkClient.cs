using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using RIoT2.Net.Devices.Models;

namespace RIoT2.Net.Devices.Services.MyUplink
{
    /// <summary>Sends one HTTP request. The device's <c>SendAsync</c>, which the tests replace.</summary>
    internal delegate Task<HttpResponseMessage> MyUplinkSend(HttpRequestMessage request, CancellationToken cancellationToken);

    /// <summary>myUplink refused the client id or secret; retrying doesn't help.</summary>
    internal sealed class MyUplinkAuthenticationException(string message) : Exception(message);

    /// <summary>myUplink didn't answer, limited the requests (429) or failed (5xx); a later poll may succeed.</summary>
    internal sealed class MyUplinkUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

    /// <summary>myUplink refused a request (400, 403, 404, 409) or a parameter change; retrying doesn't help.</summary>
    internal sealed class MyUplinkRefusedException(HttpStatusCode status, string message) : Exception(message)
    {
        public HttpStatusCode Status { get; } = status;
    }

    internal sealed record MyUplinkSystem(string Id, string Name, bool? HasAlarm, IReadOnlyList<MyUplinkSystemDevice> Devices);

    internal sealed record MyUplinkSystemDevice(string Id, string ProductName, bool? Connected);

    internal sealed record MyUplinkAlarms(int Count, string? Latest);

    /// <summary>
    /// The official myUplink API (api.myuplink.com) with an app's client credentials: the OAuth client credentials
    /// grant gives an access token for the app owner's systems, so no browser login or refresh token is needed. The
    /// token lives about an hour and is fetched again when it expires or myUplink answers 401. Not thread-safe: the
    /// device sends one call at a time.
    /// </summary>
    internal sealed class MyUplinkClient
    {
        internal const string ApiUrl = "https://api.myuplink.com";
        internal const string TokenUrl = ApiUrl + "/oauth/token";
        internal const string Scope = "READSYSTEM WRITESYSTEM";

        private readonly string _clientId;
        private readonly string _clientSecret;
        private readonly string _language;
        private readonly MyUplinkSend _send;
        private readonly ILogger _logger;
        private readonly TimeProvider _time;
        private string? _token;
        private DateTimeOffset _tokenExpires;

        public MyUplinkClient(string clientId, string clientSecret, string language, MyUplinkSend send, ILogger logger, TimeProvider time)
        {
            _clientId = clientId;
            _clientSecret = clientSecret;
            _language = language;
            _send = send;
            _logger = logger;
            _time = time;
        }

        /// <summary>The account's systems with their devices.</summary>
        public async Task<IReadOnlyList<MyUplinkSystem>> GetSystemsAsync(CancellationToken cancellationToken)
        {
            var json = await ApiAsync(HttpMethod.Get, "/v2/systems/me?page=1&itemsPerPage=100", null, "the systems", cancellationToken).ConfigureAwait(false);
            return Objects(json?["systems"]).Select(system => new MyUplinkSystem(
                Text(system["systemId"]) ?? "",
                Text(system["name"]) ?? "",
                Bool(system["hasAlarm"]),
                Objects(system["devices"]).Select(device => new MyUplinkSystemDevice(
                    Text(device["id"]) ?? "",
                    Text(device["product"]?["name"]) ?? "",
                    Text(device["connectionState"]) is string state ? string.Equals(state, "Connected", StringComparison.OrdinalIgnoreCase) : null))
                .Where(x => x.Id.Length > 0).ToList()))
                .Where(x => x.Id.Length > 0).ToList();
        }

        /// <summary>The device's parameters: all of them, or the ones in <paramref name="ids"/>.</summary>
        public async Task<IReadOnlyList<MyUplinkPoint>> GetPointsAsync(string deviceId, IEnumerable<string>? ids, CancellationToken cancellationToken)
        {
            var path = $"/v2/devices/{Uri.EscapeDataString(deviceId)}/points";
            var list = ids?.ToList();
            if (list is { Count: > 0 })
                path += "?parameters=" + string.Join(",", list.Select(Uri.EscapeDataString));
            var json = await ApiAsync(HttpMethod.Get, path, null, "the parameters", cancellationToken, language: true).ConfigureAwait(false);
            return Objects(json).Select(ReadPoint).Where(x => x.Id.Length > 0).ToList();
        }

        /// <summary>Writes parameters. Throws <see cref="MyUplinkRefusedException"/> when myUplink doesn't change one of them.</summary>
        public async Task SetPointsAsync(string deviceId, IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken)
        {
            var body = JsonSerializer.Serialize(values);
            var json = await ApiAsync(HttpMethod.Patch, $"/v2/devices/{Uri.EscapeDataString(deviceId)}/points", body,
                "the parameter change", cancellationToken).ConfigureAwait(false);
            foreach (var id in values.Keys)
            {
                var status = Text(json?[id]);
                if (status != null && !string.Equals(status, "modified", StringComparison.OrdinalIgnoreCase))
                    throw new MyUplinkRefusedException(HttpStatusCode.MultiStatus, $"myUplink did not change parameter {id}: {status}.");
            }
        }

        /// <summary>The system's smart home mode, or null when the system has none.</summary>
        public async Task<string?> GetSmartHomeModeAsync(string systemId, CancellationToken cancellationToken)
        {
            var json = await OptionalAsync($"/v2/systems/{Uri.EscapeDataString(systemId)}/smart-home-mode", "the smart home mode", false, cancellationToken)
                .ConfigureAwait(false);
            return Text(json?["smartHomeMode"]);
        }

        public Task SetSmartHomeModeAsync(string systemId, string mode, CancellationToken cancellationToken) =>
            ApiAsync(HttpMethod.Put, $"/v2/systems/{Uri.EscapeDataString(systemId)}/smart-home-mode",
                JsonSerializer.Serialize(new { smartHomeMode = mode }), "the smart home mode change", cancellationToken);

        /// <summary>The system's active alarms: how many, and the newest one's header. Null when myUplink refuses them.</summary>
        public async Task<MyUplinkAlarms?> GetActiveAlarmsAsync(string systemId, CancellationToken cancellationToken)
        {
            var json = await OptionalAsync($"/v2/systems/{Uri.EscapeDataString(systemId)}/notifications/active?page=1&itemsPerPage=10",
                "the alarms", true, cancellationToken).ConfigureAwait(false);
            if (json == null)
                return null;
            var alarms = Objects(json["notifications"]).ToList();
            var count = (int?)Number(json["numItems"]) ?? alarms.Count;
            var latest = alarms
                .OrderByDescending(x => Time(x["createdDatetime"]) ?? DateTimeOffset.MinValue)
                .Select(x => Text(x["header"]))
                .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
            return new MyUplinkAlarms(count, latest);
        }

        /// <summary>Whether the system has an active Premium Manage subscription; null when myUplink doesn't say.</summary>
        public async Task<bool?> GetPremiumManageAsync(string systemId, CancellationToken cancellationToken)
        {
            var json = await OptionalAsync($"/v2/systems/{Uri.EscapeDataString(systemId)}/subscriptions", "the subscriptions", false, cancellationToken)
                .ConfigureAwait(false);
            if (json == null)
                return null;
            var now = _time.GetUtcNow();
            IEnumerable<JsonObject> responses = json is JsonObject single ? [single] : Objects(json);
            return responses.SelectMany(x => Objects(x["subscriptions"])).Any(x =>
                Text(x["type"])?.ToLowerInvariant() is "manage" or "premium" && (Time(x["validUntil"]) is not DateTimeOffset until || until > now));
        }

        internal static MyUplinkPoint ReadPoint(JsonObject json)
        {
            // Min and max are in raw units: scaleValue (e.g. "0.1") turns them into the value's units.
            var scale = Number(json["scaleValue"]) is double s && s > 0 ? s : 1;
            return new MyUplinkPoint
            {
                Id = Text(json["parameterId"]) ?? "",
                Name = Text(json["parameterName"])?.Trim() ?? "",
                Unit = Text(json["parameterUnit"])?.Trim() ?? "",
                Writable = Bool(json["writable"]) ?? false,
                Value = Number(json["value"]),
                Text = Text(json["strVal"]),
                Minimum = Number(json["minValue"]) is double min ? Math.Round(min * scale, 6) : null,
                Maximum = Number(json["maxValue"]) is double max ? Math.Round(max * scale, 6) : null,
                Step = Number(json["stepValue"]) is double step && step > 0 ? step : null,
                Enum = Objects(json["enumValues"])
                    .Select(x => new MyUplinkEnumValue(Text(x["value"]) ?? "", Text(x["text"])?.Trim() ?? ""))
                    .Where(x => x.Value.Length > 0 && x.Text.Length > 0)
                    .ToList()
            };
        }

        // A GET whose refusal (403, 404) means the system doesn't have it: null instead of an error.
        private async Task<JsonNode?> OptionalAsync(string path, string what, bool language, CancellationToken cancellationToken)
        {
            try
            {
                return await ApiAsync(HttpMethod.Get, path, null, what, cancellationToken, language).ConfigureAwait(false);
            }
            catch (MyUplinkRefusedException error)
            {
                _logger.LogDebug("myUplink refused {What}: {Reason}", what, error.Message);
                return null;
            }
        }

        private async Task<JsonNode?> ApiAsync(HttpMethod method, string path, string? body, string what, CancellationToken cancellationToken, bool language = false)
        {
            for (var attempt = 0; ; attempt++)
            {
                var token = await TokenAsync(cancellationToken).ConfigureAwait(false);
                using var request = new HttpRequestMessage(method, ApiUrl + path);
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
                request.Headers.TryAddWithoutValidation("Accept", "application/json");
                if (language)
                    request.Headers.TryAddWithoutValidation("Accept-Language", _language);
                if (body != null)
                    request.Content = new StringContent(body, Encoding.UTF8, "application/json");

                using var response = await SendAsync(request, what, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    _token = null;
                    if (attempt == 0)
                        continue;
                    throw new MyUplinkAuthenticationException(
                        "myUplink accepted the client id and secret but refuses the token; check that the app has the READSYSTEM and WRITESYSTEM scopes.");
                }
                await EnsureSuccessAsync(response, what, cancellationToken).ConfigureAwait(false);
                return await ReadJsonAsync(response, what, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task<string> TokenAsync(CancellationToken cancellationToken)
        {
            if (_token != null && _time.GetUtcNow() < _tokenExpires)
                return _token;

            var form = $"grant_type=client_credentials&client_id={Uri.EscapeDataString(_clientId)}" +
                $"&client_secret={Uri.EscapeDataString(_clientSecret)}&scope={Uri.EscapeDataString(Scope)}";
            using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
            {
                Content = new StringContent(form, Encoding.UTF8, "application/x-www-form-urlencoded")
            };
            using var response = await SendAsync(request, "the token", cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
            {
                var error = Text((await ReadJsonOrNullAsync(response, cancellationToken).ConfigureAwait(false))?["error"]);
                throw new MyUplinkAuthenticationException(
                    $"myUplink refused the client id or secret ({error ?? ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)}); check the app at dev.myuplink.com.");
            }
            await EnsureSuccessAsync(response, "the token", cancellationToken).ConfigureAwait(false);

            var json = await ReadJsonAsync(response, "the token", cancellationToken).ConfigureAwait(false);
            var token = Text(json?["access_token"]);
            if (string.IsNullOrEmpty(token))
                throw new MyUplinkUnavailableException("myUplink's token answer has no access token.");
            // Renew a minute early, so a token doesn't expire between the check and the call.
            var lifetime = Number(json?["expires_in"]) is double seconds && seconds > 120 ? seconds : 3600;
            _token = token;
            _tokenExpires = _time.GetUtcNow().AddSeconds(lifetime - 60);
            return token;
        }

        private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, string what, CancellationToken cancellationToken)
        {
            try
            {
                return await _send(request, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException error)
            {
                throw new MyUplinkUnavailableException($"Could not reach myUplink for {what}: {error.Message}", error);
            }
            catch (TimeoutException error)
            {
                throw new MyUplinkUnavailableException($"myUplink did not answer for {what}.", error);
            }
            catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
            {
                throw new MyUplinkUnavailableException($"myUplink did not answer in time for {what}.", error);
            }
        }

        private static async Task EnsureSuccessAsync(HttpResponseMessage response, string what, CancellationToken cancellationToken)
        {
            if (response.IsSuccessStatusCode)
                return;
            var status = (int)response.StatusCode;
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retry = response.Headers.RetryAfter?.Delta is TimeSpan delta ? $" Retry after {delta.TotalSeconds:0} s." : "";
                throw new MyUplinkUnavailableException($"myUplink is limiting requests (429) for {what}; the next poll tries again.{retry}");
            }
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (body.Length > 200)
                body = body[..200];
            var message = $"myUplink answered {status} {response.ReasonPhrase} for {what}. {body}".TrimEnd();
            if (status >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout)
                throw new MyUplinkUnavailableException(message);
            throw new MyUplinkRefusedException(response.StatusCode, message);
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
                throw new MyUplinkUnavailableException($"myUplink's answer for {what} is not JSON.", error);
            }
        }

        private static async Task<JsonNode?> ReadJsonOrNullAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            try
            {
                return await ReadJsonAsync(response, "the token", cancellationToken).ConfigureAwait(false);
            }
            catch (MyUplinkUnavailableException)
            {
                return null;
            }
        }

        private static IEnumerable<JsonObject> Objects(JsonNode? node) => node is JsonArray array ? array.OfType<JsonObject>() : [];

        private static double? Number(JsonNode? node)
        {
            if (node is not JsonValue value)
                return null;
            if (value.TryGetValue<double>(out var number))
                return number;
            if (value.TryGetValue<string>(out var text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                return number;
            return null;
        }

        private static bool? Bool(JsonNode? node)
        {
            if (node is not JsonValue value)
                return null;
            if (value.TryGetValue<bool>(out var flag))
                return flag;
            if (value.TryGetValue<string>(out var text) && bool.TryParse(text, out flag))
                return flag;
            return null;
        }

        private static string? Text(JsonNode? node)
        {
            if (node is not JsonValue value)
                return null;
            if (value.TryGetValue<string>(out var text))
                return text;
            return Number(node)?.ToString(CultureInfo.InvariantCulture);
        }

        private static DateTimeOffset? Time(JsonNode? node) =>
            DateTimeOffset.TryParse(Text(node), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time) ? time : null;
    }
}
