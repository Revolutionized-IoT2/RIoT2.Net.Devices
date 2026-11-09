using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RIoT2.Net.Devices.Models;

namespace RIoT2.Net.Devices.Services.Govee
{
    /// <summary>Sends one HTTP request. The device's <c>SendAsync</c>, which the tests replace.</summary>
    internal delegate Task<HttpResponseMessage> GoveeSend(HttpRequestMessage request, CancellationToken cancellationToken);

    /// <summary>Govee refused the API key (401, 403); retrying doesn't help.</summary>
    internal sealed class GoveeAuthenticationException(string message) : Exception(message);

    /// <summary>Govee didn't answer, limited the requests (429) or failed (5xx); a later poll may succeed.</summary>
    internal sealed class GoveeUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

    /// <summary>Govee refused a request (400, 404, ...), for example the state of a device it doesn't support.</summary>
    internal sealed class GoveeRefusedException(int status, string message) : Exception(message)
    {
        public int Status { get; } = status;
    }

    /// <summary>
    /// The Govee Open API (openapi.api.govee.com) with the account's API key in the <c>Govee-API-Key</c> header. Govee
    /// limits the device list to 30 requests a minute per account and the state to 30 a minute per device. The key is
    /// never logged or put into an error.
    /// </summary>
    internal sealed class GoveeClient
    {
        internal const string ApiUrl = "https://openapi.api.govee.com";
        internal const string DevicesPath = "router/api/v1/user/devices";
        internal const string StatePath = "router/api/v1/device/state";

        internal const string TemperatureInstance = "sensorTemperature";
        internal const string HumidityInstance = "sensorHumidity";
        private const string PropertyType = "devices.capabilities.property";
        private const string OnlineType = "devices.capabilities.online";
        private const string ThermometerType = "devices.types.thermometer";

        private readonly string _apiKey;
        private readonly string _baseUrl;
        private readonly GoveeSend _send;

        public GoveeClient(string apiKey, string? endpoint, GoveeSend send)
        {
            _apiKey = apiKey;
            _baseUrl = (string.IsNullOrWhiteSpace(endpoint) ? ApiUrl : endpoint.Trim()).TrimEnd('/');
            _send = send;
        }

        /// <summary>Every device on the account, with what its capabilities say it measures.</summary>
        public async Task<List<GoveeSensor>> GetDevicesAsync(CancellationToken cancellationToken)
        {
            var json = await ApiAsync(HttpMethod.Get, DevicesPath, null, "the device list", cancellationToken).ConfigureAwait(false);
            return Objects(json?["data"]).Select(ReadDevice).Where(x => x.Device.Length > 0).ToList();
        }

        /// <summary>The device's state; temperatures are turned into °C from °F when <paramref name="fahrenheit"/> is set.</summary>
        public async Task<GoveeState> GetStateAsync(GoveeSensor sensor, bool fahrenheit, CancellationToken cancellationToken)
        {
            var body = new JsonObject
            {
                ["requestId"] = Guid.NewGuid().ToString(),
                ["payload"] = new JsonObject { ["sku"] = sensor.Sku, ["device"] = sensor.Device }
            }.ToJsonString();
            var json = await ApiAsync(HttpMethod.Post, StatePath, body, $"the state of {sensor.Name}", cancellationToken).ConfigureAwait(false);
            return ReadState(json?["payload"] as JsonObject, sensor.BatteryInstance, fahrenheit);
        }

        internal static GoveeSensor ReadDevice(JsonObject json)
        {
            var sensor = new GoveeSensor
            {
                Device = Text(json["device"])?.Trim() ?? "",
                Sku = Text(json["sku"])?.Trim() ?? "",
                Type = Text(json["type"]) ?? ""
            };
            sensor.Name = Text(json["deviceName"])?.Trim() is { Length: > 0 } name ? name : $"Govee {(sensor.Sku.Length > 0 ? sensor.Sku : sensor.Device)}";
            foreach (var capability in Objects(json["capabilities"]))
            {
                if (!string.Equals(Text(capability["type"]), PropertyType, StringComparison.OrdinalIgnoreCase) || Text(capability["instance"]) is not string instance)
                    continue;
                sensor.Properties.Add(instance);
                if (string.Equals(instance, TemperatureInstance, StringComparison.OrdinalIgnoreCase))
                    sensor.HasTemperature = true;
                else if (string.Equals(instance, HumidityInstance, StringComparison.OrdinalIgnoreCase))
                    sensor.HasHumidity = true;
                else if (instance.Contains("battery", StringComparison.OrdinalIgnoreCase))
                    sensor.BatteryInstance ??= instance;
            }
            // A thermometer measures temperature even when its capabilities don't list it.
            if (string.Equals(sensor.Type, ThermometerType, StringComparison.OrdinalIgnoreCase))
                sensor.HasTemperature = true;
            return sensor;
        }

        internal static GoveeState ReadState(JsonObject? payload, string? batteryInstance, bool fahrenheit)
        {
            var state = new GoveeState();
            foreach (var capability in Objects(payload?["capabilities"]))
            {
                var type = Text(capability["type"]);
                var instance = Text(capability["instance"]) ?? "";
                var value = (capability["state"] as JsonObject)?["value"];
                if (string.Equals(type, OnlineType, StringComparison.OrdinalIgnoreCase))
                    state.Online = Bool(value);
                else if (string.Equals(instance, TemperatureInstance, StringComparison.OrdinalIgnoreCase))
                    state.Temperature = Number(value) is double temperature ? Celsius(temperature, fahrenheit) : null;
                else if (string.Equals(instance, HumidityInstance, StringComparison.OrdinalIgnoreCase))
                    state.Humidity = Number(value) is double humidity ? Math.Round(humidity, 1) : null;
                else if (string.Equals(instance, batteryInstance, StringComparison.OrdinalIgnoreCase)
                    || instance.Contains("battery", StringComparison.OrdinalIgnoreCase))
                    state.Battery ??= Number(value);
            }
            return state;
        }

        /// <summary>°C: to a tenth from °F (the sensors' resolution), to a hundredth otherwise.</summary>
        internal static double Celsius(double value, bool fahrenheit) =>
            fahrenheit ? Math.Round((value - 32) * 5 / 9, 1) : Math.Round(value, 2);

        private async Task<JsonNode?> ApiAsync(HttpMethod method, string path, string? body, string what, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(method, $"{_baseUrl}/{path}");
            request.Headers.TryAddWithoutValidation("Govee-API-Key", _apiKey);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            if (body != null)
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using var response = await SendAsync(request, what, cancellationToken).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            JsonNode? json = null;
            try
            {
                json = string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
            }
            catch (JsonException error) when (response.IsSuccessStatusCode)
            {
                throw new GoveeUnavailableException($"Govee's answer for {what} is not JSON.", error);
            }
            catch (JsonException)
            {
                // An error page; the status says what happened.
            }

            // Govee also puts a status into the body ("code"), sometimes with HTTP 200.
            var status = (int)response.StatusCode;
            if (response.IsSuccessStatusCode && Number(json?["code"]) is double code && code != 200)
                status = (int)code;
            if (status is >= 200 and < 300)
                return json;

            var message = Text(json?["msg"]) ?? Text(json?["message"]) ?? (text.Length > 200 ? text[..200] : text);
            if (status is (int)HttpStatusCode.Unauthorized or (int)HttpStatusCode.Forbidden)
                throw new GoveeAuthenticationException($"Govee refused the API key for {what}; check the apiKey. {message}".TrimEnd());
            if (status == (int)HttpStatusCode.TooManyRequests)
                throw new GoveeUnavailableException($"Govee is limiting requests (429) for {what}; the next poll tries again.");
            var reason = $"Govee answered {status} for {what}. {message}".TrimEnd();
            if (status >= 500 || status == (int)HttpStatusCode.RequestTimeout)
                throw new GoveeUnavailableException(reason);
            throw new GoveeRefusedException(status, reason);
        }

        private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, string what, CancellationToken cancellationToken)
        {
            try
            {
                return await _send(request, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException error)
            {
                throw new GoveeUnavailableException($"Could not reach Govee for {what}: {error.Message}", error);
            }
            catch (TimeoutException error)
            {
                throw new GoveeUnavailableException($"Govee did not answer for {what}.", error);
            }
            catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
            {
                throw new GoveeUnavailableException($"Govee did not answer in time for {what}.", error);
            }
        }

        private static IEnumerable<JsonObject> Objects(JsonNode? node) => node is JsonArray array ? array.OfType<JsonObject>() : [];

        /// <summary>A number, a number in text, or an object's first number; null for "" (Govee's "can't be read").</summary>
        internal static double? Number(JsonNode? node)
        {
            switch (node)
            {
                case JsonValue value when value.GetValueKind() == JsonValueKind.Number:
                    return value.GetValue<double>();
                case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                    return double.TryParse(value.GetValue<string>(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : null;
                case JsonObject json:
                    foreach (var (_, member) in json)
                    {
                        if (Number(member) is double first)
                            return first;
                    }
                    return null;
                default:
                    return null;
            }
        }

        private static bool? Bool(JsonNode? node)
        {
            if (node is not JsonValue value)
                return null;
            return value.GetValueKind() switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => bool.TryParse(value.GetValue<string>(), out var flag) ? flag : null,
                JsonValueKind.Number => value.GetValue<double>() != 0,
                _ => null
            };
        }

        private static string? Text(JsonNode? node)
        {
            if (node is not JsonValue value)
                return null;
            return value.GetValueKind() switch
            {
                JsonValueKind.String => value.GetValue<string>(),
                JsonValueKind.Number => value.GetValue<double>().ToString(CultureInfo.InvariantCulture),
                _ => null
            };
        }
    }
}
