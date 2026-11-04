using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using RIoT2.Net.Devices.Models;

namespace RIoT2.Net.Devices.Services.Harvia
{
    /// <summary>Sends one HTTP request. The device's <c>SendAsync</c>, which the tests replace.</summary>
    internal delegate Task<HttpResponseMessage> HarviaSend(HttpRequestMessage request, CancellationToken cancellationToken);

    /// <summary>MyHarvia refused the login; the e-mail or password has to be fixed.</summary>
    internal sealed class HarviaAuthenticationException(string message) : Exception(message);

    /// <summary>MyHarvia didn't answer, limited the requests or failed; a later try may succeed.</summary>
    internal sealed class HarviaUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

    /// <summary>MyHarvia refused a request (4xx or a GraphQL error); retrying doesn't help.</summary>
    internal sealed class HarviaRefusedException(string message) : Exception(message);

    /// <summary>The AppSync GraphQL endpoints of the MyHarvia services and the Cognito pool of its app.</summary>
    internal sealed record HarviaEndpoints(string Users, string Device, string Data, string UserPoolId, string ClientId);

    /// <summary>The logged-in user: the push subscriptions are addressed to the e-mail and the organization.</summary>
    internal sealed record HarviaUser(string? Email, string? OrganizationId)
    {
        public IReadOnlyList<string> Receivers =>
            new[] { OrganizationId, Email }.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>A sauna's reported state or telemetry, from a query or a push.</summary>
    internal sealed record HarviaUpdate(string DeviceId, JsonObject? State, JsonObject? Telemetry, DateTimeOffset? At);

    /// <summary>What is kept of a login across restarts: the refresh token, never the password.</summary>
    internal sealed class HarviaStoredTokens
    {
        public string? RefreshToken { get; set; }
    }

    /// <summary>
    /// The MyHarvia cloud of Xenio WiFi panels, the API the MyHarvia app uses: endpoint discovery at
    /// prod.myharvia-cloud.net, a Cognito login, and AppSync GraphQL with the ID token (no <c>Bearer</c>). One login at a
    /// time, so the push connections and a poll don't log in twice. The refresh token is kept in a file.
    /// </summary>
    internal sealed class HarviaClient
    {
        internal const string DiscoveryUrl = "https://prod.myharvia-cloud.net";

        /// <summary>An ID token that expires sooner than this is renewed before it is used.</summary>
        internal static readonly TimeSpan RenewMargin = TimeSpan.FromMinutes(2);

        internal const string UserQuery = "query Query {\n  getCurrentUserDetails {\n    email\n    organizationId\n    __typename\n  }\n}\n";
        internal const string TreeQuery = "query Query {\n  getDeviceTree\n}\n";
        internal const string StateQuery =
            "query Query($deviceId: ID!) {\n  getDeviceState(deviceId: $deviceId) {\n    desired\n    reported\n    timestamp\n    __typename\n  }\n}\n";
        internal const string DataQuery =
            "query Query($deviceId: String!) {\n  getLatestData(deviceId: $deviceId) {\n    deviceId\n    timestamp\n    sessionId\n    type\n    data\n    __typename\n  }\n}\n";
        internal const string StateChange =
            "mutation Mutation($deviceId: ID!, $state: AWSJSON!, $getFullState: Boolean) {\n  requestStateChange(deviceId: $deviceId, state: $state, getFullState: $getFullState)\n}\n";
        internal const string StateSubscription =
            "subscription Subscription($receiver: String!) {\n  onStateUpdated(receiver: $receiver) {\n    desired\n    reported\n    timestamp\n    receiver\n    __typename\n  }\n}\n";
        internal const string DataSubscription =
            "subscription Subscription($receiver: String!) {\n  onDataUpdates(receiver: $receiver) {\n    item {\n      deviceId\n      timestamp\n      sessionId\n      type\n      data\n      __typename\n    }\n    __typename\n  }\n}\n";

        private readonly string _email;
        private readonly string _password;
        private readonly HarviaSend _send;
        private readonly ILogger _logger;
        private readonly TimeProvider _time;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private HarviaEndpoints? _endpoints;
        private CognitoTokens? _tokens;
        private HarviaUser? _user;

        public HarviaClient(string email, string password, HarviaSend send, string tokenFile, ILogger logger, TimeProvider time)
        {
            _email = email;
            _password = password;
            _send = send;
            TokenFile = tokenFile;
            _logger = logger;
            _time = time;
        }

        public string TokenFile { get; }

        /// <summary>The service endpoints and the Cognito pool, read once (again after a failure).</summary>
        public async Task<HarviaEndpoints> GetEndpointsAsync(CancellationToken cancellationToken)
        {
            if (_endpoints != null)
                return _endpoints;
            var users = await DiscoverAsync("users", cancellationToken).ConfigureAwait(false);
            var device = await DiscoverAsync("device", cancellationToken).ConfigureAwait(false);
            var data = await DiscoverAsync("data", cancellationToken).ConfigureAwait(false);
            var pool = HarviaJson.Text(users["userPoolId"]);
            var client = HarviaJson.Text(users["clientId"]);
            if (pool == null || client == null)
                throw new HarviaUnavailableException("MyHarvia's endpoint discovery has no login (userPoolId, clientId).");
            return _endpoints = new HarviaEndpoints(Endpoint(users, "users"), Endpoint(device, "device"), Endpoint(data, "data"), pool, client);
        }

        private static string Endpoint(JsonObject json, string service) =>
            HarviaJson.Text(json["endpoint"]) is string url && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
                ? url
                : throw new HarviaUnavailableException($"MyHarvia's endpoint discovery has no https endpoint for {service}.");

        private async Task<JsonObject> DiscoverAsync(string service, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{DiscoveryUrl}/{service}/endpoint");
            using var response = await SendAsync(_send, request, "the endpoint discovery", cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new HarviaUnavailableException($"MyHarvia answered {(int)response.StatusCode} {response.ReasonPhrase} for the {service} endpoint.");
            return await ReadJsonAsync(response, "the endpoint discovery", cancellationToken).ConfigureAwait(false) as JsonObject
                ?? throw new HarviaUnavailableException($"MyHarvia's {service} endpoint isn't a JSON object.");
        }

        /// <summary>
        /// A valid ID token. <paramref name="rejected"/> is a token MyHarvia refused: then a new one is fetched, unless
        /// another caller already did. Refreshes with the refresh token, and logs in with the password when that fails.
        /// </summary>
        public async Task<string> GetIdTokenAsync(string? rejected, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var now = _time.GetUtcNow();
                if (_tokens != null && (rejected == null ? _tokens.ExpiresAt - now > RenewMargin : _tokens.IdToken != rejected))
                    return _tokens.IdToken;

                var endpoints = await GetEndpointsAsync(cancellationToken).ConfigureAwait(false);
                var cognito = new CognitoSrp(endpoints.UserPoolId, endpoints.ClientId, _send, _time);
                if ((_tokens?.RefreshToken ?? Load()?.RefreshToken) is string refresh)
                {
                    try
                    {
                        return Store(await cognito.RefreshAsync(refresh, cancellationToken).ConfigureAwait(false));
                    }
                    catch (HarviaAuthenticationException error)
                    {
                        _logger.LogInformation("MyHarvia refused the refresh token; logging in again. {Reason}", error.Message);
                        _tokens = null;
                        Save(new HarviaStoredTokens());
                    }
                    catch (HarviaUnavailableException) when (rejected == null && _tokens != null && _tokens.ExpiresAt > now)
                    {
                        // The token still works for a while; renew it next time.
                        return _tokens.IdToken;
                    }
                }

                try
                {
                    return Store(await cognito.LoginAsync(_email, _password, cancellationToken).ConfigureAwait(false));
                }
                catch (HarviaAuthenticationException error)
                {
                    throw new HarviaAuthenticationException($"MyHarvia refused the login of {Mask(_email)}; check the e-mail and password. {error.Message}");
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        private string Store(CognitoTokens tokens)
        {
            _tokens = tokens;
            Save(new HarviaStoredTokens { RefreshToken = tokens.RefreshToken });
            _logger.LogDebug("MyHarvia login renewed; the ID token is valid until {Expires:u}.", tokens.ExpiresAt);
            return tokens.IdToken;
        }

        /// <summary>The logged-in user, read once.</summary>
        public async Task<HarviaUser> GetUserAsync(CancellationToken cancellationToken)
        {
            if (_user != null)
                return _user;
            var endpoints = await GetEndpointsAsync(cancellationToken).ConfigureAwait(false);
            var data = await GraphQLAsync(endpoints.Users, UserQuery, [], "the user", cancellationToken).ConfigureAwait(false);
            var user = data?["getCurrentUserDetails"];
            return _user = new HarviaUser(HarviaJson.Text(user?["email"]), HarviaJson.Text(user?["organizationId"]));
        }

        /// <summary>The ids of the account's saunas: the leaves of the device tree.</summary>
        public async Task<List<string>> GetDeviceIdsAsync(CancellationToken cancellationToken)
        {
            var endpoints = await GetEndpointsAsync(cancellationToken).ConfigureAwait(false);
            var data = await GraphQLAsync(endpoints.Device, TreeQuery, [], "the device tree", cancellationToken).ConfigureAwait(false);
            return DeviceIds(data?["getDeviceTree"]);
        }

        /// <summary>The device ids in a device tree, a JSON string of nodes with children (<c>c</c>) and info (<c>i.name</c>).</summary>
        internal static List<string> DeviceIds(JsonNode? tree)
        {
            JsonNode? root = tree;
            if (tree is JsonValue value && value.TryGetValue<string>(out var text))
            {
                try
                {
                    root = JsonNode.Parse(text);
                }
                catch (JsonException)
                {
                    throw new HarviaUnavailableException("MyHarvia's device tree is not JSON.");
                }
            }
            var ids = new List<string>();
            void Walk(JsonNode? node)
            {
                switch (node)
                {
                    case JsonArray array:
                        foreach (var item in array)
                            Walk(item);
                        break;
                    case JsonObject json when json["c"] is JsonArray children:
                        Walk(children);
                        break;
                    case JsonObject json:
                        if ((HarviaJson.Text(json["i"]?["name"]) ?? HarviaJson.Text(json["deviceId"])) is string id && id.Length > 0 && !ids.Contains(id))
                            ids.Add(id);
                        break;
                }
            }
            Walk(root);
            return ids;
        }

        /// <summary>A sauna's reported state (its shadow).</summary>
        public async Task<HarviaUpdate> GetStateAsync(string deviceId, CancellationToken cancellationToken)
        {
            var endpoints = await GetEndpointsAsync(cancellationToken).ConfigureAwait(false);
            var data = await GraphQLAsync(endpoints.Device, StateQuery, new JsonObject { ["deviceId"] = deviceId }, "the sauna's state", cancellationToken)
                .ConfigureAwait(false);
            var state = data?["getDeviceState"];
            return new HarviaUpdate(deviceId, HarviaJson.Object(state?["reported"]) ?? [], null, HarviaJson.Time(state?["timestamp"]));
        }

        /// <summary>A sauna's latest telemetry, or null when it has sent none.</summary>
        public async Task<HarviaUpdate?> GetLatestDataAsync(string deviceId, CancellationToken cancellationToken)
        {
            var endpoints = await GetEndpointsAsync(cancellationToken).ConfigureAwait(false);
            var data = await GraphQLAsync(endpoints.Data, DataQuery, new JsonObject { ["deviceId"] = deviceId }, "the sauna's measurements", cancellationToken)
                .ConfigureAwait(false);
            var latest = data?["getLatestData"];
            return HarviaJson.Object(latest?["data"]) is JsonObject telemetry
                ? new HarviaUpdate(deviceId, null, telemetry, HarviaJson.Time(latest?["timestamp"]))
                : null;
        }

        /// <summary>Asks the sauna to change its state, e.g. <c>{"active": 1}</c>.</summary>
        public async Task RequestStateChangeAsync(string deviceId, JsonObject state, CancellationToken cancellationToken)
        {
            var endpoints = await GetEndpointsAsync(cancellationToken).ConfigureAwait(false);
            await GraphQLAsync(endpoints.Device, StateChange, new JsonObject
            {
                ["deviceId"] = deviceId,
                ["state"] = state.ToJsonString(),
                ["getFullState"] = false
            }, "the change", cancellationToken).ConfigureAwait(false);
        }

        private async Task<JsonNode?> GraphQLAsync(string url, string query, JsonObject variables, string what, CancellationToken cancellationToken)
        {
            string? rejected = null;
            for (var attempt = 0; ; attempt++)
            {
                var token = await GetIdTokenAsync(rejected, cancellationToken).ConfigureAwait(false);
                using var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(new JsonObject
                    {
                        ["operationName"] = query.StartsWith("mutation", StringComparison.Ordinal) ? "Mutation" : "Query",
                        ["variables"] = variables.DeepClone(),
                        ["query"] = query
                    }.ToJsonString(), Encoding.UTF8, "application/json")
                };
                request.Headers.TryAddWithoutValidation("authorization", token);
                using var response = await SendAsync(_send, request, what, cancellationToken).ConfigureAwait(false);
                var unauthorized = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
                JsonObject? json = null;
                if (!unauthorized)
                {
                    var status = (int)response.StatusCode;
                    if (status >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests)
                        throw new HarviaUnavailableException($"MyHarvia answered {status} {response.ReasonPhrase} for {what}.");
                    json = await ReadJsonAsync(response, what, cancellationToken).ConfigureAwait(false) as JsonObject;
                    var errors = (json?["errors"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
                    unauthorized = errors.Any(e => HarviaJson.Text(e["errorType"])?.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase) == true);
                    if (!unauthorized && errors.Count > 0)
                        throw new HarviaRefusedException($"MyHarvia refused {what}: {string.Join("; ", errors.Select(e => HarviaJson.Text(e["message"]) ?? HarviaJson.Text(e["errorType"])))}");
                    if (!unauthorized && !response.IsSuccessStatusCode)
                        throw new HarviaRefusedException($"MyHarvia answered {status} {response.ReasonPhrase} for {what}.");
                }
                if (unauthorized)
                {
                    if (attempt == 0)
                    {
                        _logger.LogDebug("MyHarvia refused the token for {What}; getting a new one.", what);
                        rejected = token;
                        continue;
                    }
                    throw new HarviaAuthenticationException($"MyHarvia refused the login for {what}.");
                }
                return json?["data"];
            }
        }

        internal static async Task<HttpResponseMessage> SendAsync(HarviaSend send, HttpRequestMessage request, string what, CancellationToken cancellationToken)
        {
            try
            {
                return await send(request, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException error)
            {
                throw new HarviaUnavailableException($"Could not reach MyHarvia for {what}: {error.Message}", error);
            }
            catch (TimeoutException error)
            {
                throw new HarviaUnavailableException($"MyHarvia did not answer for {what}.", error);
            }
            catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
            {
                throw new HarviaUnavailableException($"MyHarvia did not answer in time for {what}.", error);
            }
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
                throw new HarviaUnavailableException($"MyHarvia's answer for {what} is not JSON.", error);
            }
        }

        /// <summary>The AppSync host of a GraphQL endpoint, which the realtime handshake names.</summary>
        internal static string Host(string graphQlUrl) => new Uri(graphQlUrl).Host;

        /// <summary>
        /// The realtime (WebSocket) address of a GraphQL endpoint: the <c>appsync-realtime-api</c> host, with the ID
        /// token and the API host as the base64 <c>header</c> and an empty <c>payload</c>.
        /// </summary>
        internal static Uri RealtimeUrl(string graphQlUrl, string idToken)
        {
            var uri = new Uri(graphQlUrl);
            var host = uri.Host.Replace(".appsync-api.", ".appsync-realtime-api.", StringComparison.Ordinal);
            var header = Convert.ToBase64String(Encoding.UTF8.GetBytes(new JsonObject { ["Authorization"] = idToken, ["host"] = uri.Host }.ToJsonString()));
            return new Uri($"wss://{host}{uri.AbsolutePath}?header={Uri.EscapeDataString(header)}&payload=e30=");
        }

        /// <summary>The <c>start</c> message of a subscription.</summary>
        internal static string StartMessage(string id, string graphQlUrl, string idToken, string query, string receiver) => new JsonObject
        {
            ["id"] = id,
            ["type"] = "start",
            ["payload"] = new JsonObject
            {
                ["data"] = new JsonObject { ["query"] = query, ["variables"] = new JsonObject { ["receiver"] = receiver } }.ToJsonString(),
                ["extensions"] = new JsonObject
                {
                    ["authorization"] = new JsonObject { ["Authorization"] = idToken, ["host"] = Host(graphQlUrl) }
                }
            }
        }.ToJsonString();

        /// <summary>The sauna update in a pushed <c>data</c> message's <c>payload.data</c>, or null.</summary>
        internal static HarviaUpdate? ParseUpdate(JsonNode? data)
        {
            if (data?["onStateUpdated"] is JsonObject state)
            {
                var reported = HarviaJson.Object(state["reported"]);
                var id = HarviaJson.Text(reported?["deviceId"]);
                return reported != null && !string.IsNullOrEmpty(id) ? new HarviaUpdate(id, reported, null, HarviaJson.Time(state["timestamp"])) : null;
            }
            if (data?["onDataUpdates"]?["item"] is JsonObject item)
            {
                var telemetry = HarviaJson.Object(item["data"]);
                var id = HarviaJson.Text(item["deviceId"]);
                return telemetry != null && !string.IsNullOrEmpty(id) ? new HarviaUpdate(id, null, telemetry, HarviaJson.Time(item["timestamp"])) : null;
            }
            return null;
        }

        internal static string Mask(string email)
        {
            var at = email.IndexOf('@');
            return at <= 0 ? "***" : $"{email[0]}***@{(at + 1 < email.Length ? email[at + 1] : '*')}***";
        }

        private HarviaStoredTokens? Load()
        {
            try
            {
                return File.Exists(TokenFile) ? JsonSerializer.Deserialize<HarviaStoredTokens>(File.ReadAllText(TokenFile)) : null;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                _logger.LogWarning(error, "Could not read the MyHarvia token file {File}; logging in again.", TokenFile);
                return null;
            }
        }

        private void Save(HarviaStoredTokens tokens)
        {
            try
            {
                var directory = Path.GetDirectoryName(TokenFile);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);
                var temporary = TokenFile + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(tokens));
                File.Move(temporary, TokenFile, overwrite: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(error, "Could not save the MyHarvia token file {File}; the next start logs in again.", TokenFile);
            }
        }
    }
}
