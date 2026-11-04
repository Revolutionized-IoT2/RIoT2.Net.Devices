using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using RIoT2.Net.Devices.Models;

namespace RIoT2.Net.Devices.Services.Miele
{
    /// <summary>Sends one HTTP request. The device's <c>SendAsync</c>, which the tests replace.</summary>
    internal delegate Task<HttpResponseMessage> MieleSend(HttpRequestMessage request, HttpCompletionOption completion, CancellationToken cancellationToken);

    /// <summary>Miele refused the login; the user has to log in again or fix the configuration.</summary>
    internal sealed class MieleAuthenticationException(string message) : Exception(message);

    /// <summary>Miele didn't answer, limited the requests (429) or failed (5xx); a later try may succeed.</summary>
    internal sealed class MieleUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

    /// <summary>Miele refused a request (4xx); retrying doesn't help.</summary>
    internal sealed class MieleRefusedException(HttpStatusCode status, string message) : Exception(message)
    {
        public HttpStatusCode Status { get; } = status;
    }

    /// <summary>One server-sent event: its type (<c>devices</c>, <c>actions</c>, <c>ping</c>) and data.</summary>
    internal sealed record MieleEvent(string Type, string Data);

    /// <summary>
    /// The Miele 3rd Party API (api.mcs3.miele.com/v1). Stateless apart from the shared <see cref="MieleAuth"/>, so the
    /// event stream, polls and commands can use it at the same time.
    /// </summary>
    internal sealed class MieleClient
    {
        internal const string ApiUrl = "https://api.mcs3.miele.com/v1";

        private readonly MieleAuth _auth;
        private readonly string _language;
        private readonly MieleSend _send;
        private readonly ILogger _logger;

        public MieleClient(MieleAuth auth, string language, MieleSend send, ILogger logger)
        {
            _auth = auth;
            _language = language;
            _send = send;
            _logger = logger;
        }

        /// <summary>The account's appliances, by fabrication number.</summary>
        public async Task<Dictionary<string, JsonObject>> GetDevicesAsync(CancellationToken cancellationToken)
        {
            var json = await ApiAsync(HttpMethod.Get, "/devices", null, "the appliances", cancellationToken).ConfigureAwait(false);
            return Devices(json);
        }

        public async Task<MieleActions> GetActionsAsync(string id, CancellationToken cancellationToken) =>
            MieleActions.Parse(await ApiAsync(HttpMethod.Get, $"/devices/{Uri.EscapeDataString(id)}/actions", null, "the actions", cancellationToken)
                .ConfigureAwait(false) as JsonObject);

        public async Task<List<MieleProgram>> GetProgramsAsync(string id, CancellationToken cancellationToken) =>
            MieleProgram.ParseAll(await ApiAsync(HttpMethod.Get, $"/devices/{Uri.EscapeDataString(id)}/programs", null, "the programs", cancellationToken)
                .ConfigureAwait(false));

        /// <summary>The TwinDos container levels (%) of the appliances that have them, by fabrication number.</summary>
        public async Task<Dictionary<string, (int? TwinDos1, int? TwinDos2)>> GetFillingLevelsAsync(CancellationToken cancellationToken)
        {
            var json = await ApiAsync(HttpMethod.Get, "/devices/fillingLevels", null, "the filling levels", cancellationToken).ConfigureAwait(false);
            var levels = new Dictionary<string, (int?, int?)>();
            foreach (var item in (json as JsonArray)?.OfType<JsonObject>() ?? [])
            {
                var id = MieleAppliance.Text(item["deviceId"]);
                var filling = item["fillingLevels"] as JsonObject;
                if (id != null && filling != null)
                    levels[id] = (MieleAppliance.Int(filling["twinDosContainer1FillingLevel"]), MieleAppliance.Int(filling["twinDosContainer2FillingLevel"]));
            }
            return levels;
        }

        /// <summary>Sends an action, e.g. <c>{"processAction": 1}</c>.</summary>
        public Task SendActionAsync(string id, JsonObject body, CancellationToken cancellationToken) =>
            ApiAsync(HttpMethod.Put, $"/devices/{Uri.EscapeDataString(id)}/actions", body.ToJsonString(), "the action", cancellationToken);

        /// <summary>Selects a program, e.g. <c>{"programId": 13, "temperature": 180}</c>.</summary>
        public Task SetProgramAsync(string id, JsonObject body, CancellationToken cancellationToken) =>
            ApiAsync(HttpMethod.Put, $"/devices/{Uri.EscapeDataString(id)}/programs", body.ToJsonString(), "the program", cancellationToken);

        /// <summary>Opens the event stream of all appliances; the caller reads it with <see cref="ReadEventsAsync"/> and disposes it.</summary>
        public async Task<HttpResponseMessage> OpenEventsAsync(CancellationToken cancellationToken)
        {
            string? rejected = null;
            for (var attempt = 0; ; attempt++)
            {
                var token = await _auth.GetTokenAsync(rejected, cancellationToken).ConfigureAwait(false);
                using var request = Request(HttpMethod.Get, "/devices/all/events", null, token);
                request.Headers.Accept.Clear();
                request.Headers.TryAddWithoutValidation("Accept", "text/event-stream");
                var response = await SendAsync(_send, request, HttpCompletionOption.ResponseHeadersRead, "the event stream", cancellationToken)
                    .ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                    return response;
                using (response)
                {
                    if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
                    {
                        rejected = token;
                        continue;
                    }
                    await EnsureSuccessAsync(response, "the event stream", cancellationToken).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// The events of a server-sent event stream: an <c>event:</c> line, <c>data:</c> lines and an empty line each.
        /// Throws <see cref="TimeoutException"/> when no line arrives in <paramref name="timeout"/> (Miele pings every
        /// 20 seconds), so a dead connection is noticed.
        /// </summary>
        internal static async IAsyncEnumerable<MieleEvent> ReadEventsAsync(TextReader reader, TimeSpan timeout,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            string? type = null;
            var data = new StringBuilder();
            while (true)
            {
                string? line;
                using (var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    wait.CancelAfter(timeout);
                    try
                    {
                        line = await reader.ReadLineAsync(wait.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new TimeoutException($"Miele's event stream was silent for {timeout.TotalSeconds:0} seconds.", error);
                    }
                }
                if (line == null)
                    yield break;

                if (line.Length == 0)
                {
                    if (type != null || data.Length > 0)
                        yield return new MieleEvent(type ?? "message", data.ToString());
                    type = null;
                    data.Clear();
                }
                else if (line.StartsWith("event:", StringComparison.Ordinal))
                {
                    type = line[6..].Trim();
                }
                else if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    if (data.Length > 0)
                        data.Append('\n');
                    data.Append(line[5..].TrimStart());
                }
            }
        }

        /// <summary>The appliances of a <c>/devices</c> answer or a <c>devices</c> event.</summary>
        internal static Dictionary<string, JsonObject> Devices(JsonNode? json) =>
            (json as JsonObject)?.Where(x => x.Value is JsonObject).ToDictionary(x => x.Key, x => (JsonObject)x.Value!) ?? [];

        private async Task<JsonNode?> ApiAsync(HttpMethod method, string path, string? body, string what, CancellationToken cancellationToken)
        {
            string? rejected = null;
            for (var attempt = 0; ; attempt++)
            {
                var token = await _auth.GetTokenAsync(rejected, cancellationToken).ConfigureAwait(false);
                using var request = Request(method, path, body, token);
                using var response = await SendAsync(_send, request, HttpCompletionOption.ResponseContentRead, what, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
                {
                    _logger.LogDebug("Miele refused the token for {What}; getting a new one.", what);
                    rejected = token;
                    continue;
                }
                await EnsureSuccessAsync(response, what, cancellationToken).ConfigureAwait(false);
                return await ReadJsonAsync(response, what, cancellationToken).ConfigureAwait(false);
            }
        }

        private HttpRequestMessage Request(HttpMethod method, string path, string? body, string token)
        {
            var request = new HttpRequestMessage(method, $"{ApiUrl}{path}{(path.Contains('?') ? '&' : '?')}language={Uri.EscapeDataString(_language)}");
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            if (body != null)
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            return request;
        }

        internal static async Task<HttpResponseMessage> SendAsync(MieleSend send, HttpRequestMessage request, HttpCompletionOption completion, string what,
            CancellationToken cancellationToken)
        {
            try
            {
                return await send(request, completion, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException error)
            {
                throw new MieleUnavailableException($"Could not reach Miele for {what}: {error.Message}", error);
            }
            catch (TimeoutException error)
            {
                throw new MieleUnavailableException($"Miele did not answer for {what}.", error);
            }
            catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
            {
                throw new MieleUnavailableException($"Miele did not answer in time for {what}.", error);
            }
        }

        /// <summary>429, 408 and 5xx mean Miele is unavailable for now; any other failure is a refusal with Miele's message.</summary>
        internal static async Task EnsureSuccessAsync(HttpResponseMessage response, string what, CancellationToken cancellationToken)
        {
            if (response.IsSuccessStatusCode)
                return;
            var status = (int)response.StatusCode;
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new MieleUnavailableException($"Miele is limiting requests (429) for {what}; the next poll tries again.");
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            string? message = null;
            try
            {
                var json = JsonNode.Parse(body);
                message = MieleAppliance.Text(json?["message"]) ?? MieleAppliance.Text(json?["error_description"]) ?? MieleAppliance.Text(json?["error"]);
            }
            catch (JsonException)
            {
            }
            message ??= body.Length > 200 ? body[..200] : body;
            var text = $"Miele answered {status} {response.ReasonPhrase} for {what}. {message}".TrimEnd();
            if (status >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout)
                throw new MieleUnavailableException(text);
            throw new MieleRefusedException(response.StatusCode, text);
        }

        internal static async Task<JsonNode?> ReadJsonAsync(HttpResponseMessage response, string what, CancellationToken cancellationToken)
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
                throw new MieleUnavailableException($"Miele's answer for {what} is not JSON.", error);
            }
        }
    }
}
