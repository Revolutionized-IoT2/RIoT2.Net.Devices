using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using RIoT2.Core.Security;
using RIoT2.Net.Devices.Models;

namespace RIoT2.Net.Devices.Services.Harvia
{
    /// <summary>A text WebSocket; the tests script it.</summary>
    internal interface IHarviaSocket : IAsyncDisposable
    {
        Task SendAsync(string message, CancellationToken cancellationToken);

        /// <summary>The next message, or null when the server closed the connection.</summary>
        Task<string?> ReceiveAsync(CancellationToken cancellationToken);
    }

    /// <summary>Opens a <c>graphql-ws</c> WebSocket. The device's <c>ConnectAsync</c>, which the tests replace.</summary>
    internal delegate Task<IHarviaSocket> HarviaConnect(Uri url, CancellationToken cancellationToken);

    /// <summary>The push service refused the token; a new one is needed before connecting again.</summary>
    internal sealed class HarviaPushRejectedException(string message) : Exception(message);

    /// <summary>A <see cref="ClientWebSocket"/> with the <c>graphql-ws</c> subprotocol, checked against the node's outbound URL policy.</summary>
    internal sealed class HarviaWebSocket : IHarviaSocket
    {
        private static readonly TimeSpan _connectTimeout = TimeSpan.FromSeconds(30);
        private readonly ClientWebSocket _socket;

        private HarviaWebSocket(ClientWebSocket socket) => _socket = socket;

        public static async Task<IHarviaSocket> ConnectAsync(Uri url, CancellationToken cancellationToken)
        {
            // The policy judges web addresses: the same host over https.
            var check = new UriBuilder(url) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri;
            OutboundUrlPolicyHandler.Check(Core.Utils.Web.OutboundUrlPolicy, check, OutboundPurposes.Device);

            var socket = new ClientWebSocket();
            socket.Options.AddSubProtocol("graphql-ws");
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_connectTimeout);
            try
            {
                await socket.ConnectAsync(url, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
            {
                socket.Dispose();
                throw new TimeoutException($"MyHarvia's push service did not answer in {_connectTimeout.TotalSeconds:0} seconds.", error);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
            return new HarviaWebSocket(socket);
        }

        public Task SendAsync(string message, CancellationToken cancellationToken) =>
            _socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, cancellationToken);

        public async Task<string?> ReceiveAsync(CancellationToken cancellationToken)
        {
            var buffer = new byte[16 * 1024];
            using var message = new MemoryStream();
            while (true)
            {
                var result = await _socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                    return null;
                message.Write(buffer, 0, result.Count);
                if (message.Length > 4 * 1024 * 1024)
                    throw new InvalidDataException("MyHarvia's push message is larger than 4 MB.");
                if (result.EndOfMessage)
                    return Encoding.UTF8.GetString(message.ToArray());
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is WebSocketException or OperationCanceledException or ObjectDisposedException)
                {
                    // Closing is a courtesy; the socket is disposed anyway.
                }
            }
            _socket.Dispose();
        }
    }

    /// <summary>The timing of the push connections; the tests shorten it.</summary>
    internal sealed record HarviaPushSettings(TimeSpan ReconnectDelay, TimeSpan MaxReconnectDelay, TimeSpan Lifetime, TimeSpan KeepAliveTimeout)
    {
        /// <summary>
        /// A connection is renewed every 5 minutes: measured on a Xenio, pushes stop after 12-14 minutes while the
        /// keep-alives go on (WiesiDeluxe/ha-harvia-sauna, 2026-10).
        /// </summary>
        public static HarviaPushSettings Default { get; } = new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    /// <summary>
    /// One AppSync realtime connection (<c>graphql-ws</c>) to a MyHarvia service with a subscription per receiver. It
    /// reconnects when the connection is silent past the keep-alive timeout, every <see cref="HarviaPushSettings.Lifetime"/>,
    /// and after errors with a doubling delay; a refused token is renewed first. It runs until cancelled.
    /// </summary>
    internal sealed class HarviaPushConnection
    {
        private readonly string _service;
        private readonly string _endpoint;
        private readonly string _query;
        private readonly IReadOnlyList<string> _receivers;
        private readonly Func<string?, CancellationToken, Task<string>> _token;
        private readonly HarviaConnect _connect;
        private readonly Action<HarviaUpdate> _onUpdate;
        private readonly HarviaPushSettings _settings;
        private readonly ILogger _logger;

        public HarviaPushConnection(string service, string endpoint, string query, IReadOnlyList<string> receivers,
            Func<string?, CancellationToken, Task<string>> token, HarviaConnect connect, Action<HarviaUpdate> onUpdate, HarviaPushSettings settings, ILogger logger)
        {
            _service = service;
            _endpoint = endpoint;
            _query = query;
            _receivers = receivers;
            _token = token;
            _connect = connect;
            _onUpdate = onUpdate;
            _settings = settings;
            _logger = logger;
        }

        /// <summary>How many times the connection was opened, for diagnostics and tests.</summary>
        public int Connections { get; private set; }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            var delay = _settings.ReconnectDelay;
            string? rejected = null;
            while (!cancellationToken.IsCancellationRequested)
            {
                string? token = null;
                try
                {
                    token = await _token(rejected, cancellationToken).ConfigureAwait(false);
                    rejected = null;
                    if (await ListenOnceAsync(token, () => delay = _settings.ReconnectDelay, cancellationToken).ConfigureAwait(false))
                        continue;
                    _logger.LogDebug("MyHarvia closed the {Service} push connection; connecting again.", _service);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (HarviaPushRejectedException error)
                {
                    _logger.LogDebug("MyHarvia's {Service} push refused the token; renewing it: {Reason}", _service, error.Message);
                    rejected = token;
                }
                catch (HarviaAuthenticationException error)
                {
                    _logger.LogError("MyHarvia's {Service} push needs a login: {Reason}", _service, error.Message);
                    delay = _settings.MaxReconnectDelay;
                }
                catch (Exception error)
                {
                    _logger.LogWarning("MyHarvia's {Service} push failed; connecting again in {Delay}: {Reason}", _service, delay, error.Message);
                }

                try
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, _settings.MaxReconnectDelay.Ticks));
            }
        }

        // True when the connection reached its planned end, so the next one opens right away.
        private async Task<bool> ListenOnceAsync(string token, Action acknowledged, CancellationToken cancellationToken)
        {
            await using var socket = await _connect(HarviaClient.RealtimeUrl(_endpoint, token), cancellationToken).ConfigureAwait(false);
            Connections++;
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lifetime.CancelAfter(_settings.Lifetime);
            var ids = _receivers.Select((_, index) => $"{_service}-{index}-{Guid.NewGuid():N}").ToList();
            var keepAlive = _settings.KeepAliveTimeout;
            try
            {
                await socket.SendAsync("""{"type":"connection_init"}""", lifetime.Token).ConfigureAwait(false);
                while (true)
                {
                    string? text;
                    using (var wait = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
                    {
                        wait.CancelAfter(keepAlive);
                        try
                        {
                            text = await socket.ReceiveAsync(wait.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException error) when (!lifetime.IsCancellationRequested)
                        {
                            throw new TimeoutException($"MyHarvia's {_service} push was silent for {keepAlive.TotalSeconds:0} seconds.", error);
                        }
                    }
                    if (text == null)
                        return false;

                    JsonObject? message;
                    try
                    {
                        message = JsonNode.Parse(text) as JsonObject;
                    }
                    catch (JsonException)
                    {
                        _logger.LogDebug("MyHarvia's {Service} push sent a message that isn't JSON.", _service);
                        continue;
                    }

                    switch (HarviaJson.Text(message?["type"]))
                    {
                        case "connection_ack":
                            if (HarviaJson.Number(message?["payload"]?["connectionTimeoutMs"]) is double milliseconds && milliseconds > 0)
                                keepAlive = TimeSpan.FromMilliseconds(milliseconds);
                            for (var i = 0; i < _receivers.Count; i++)
                                await socket.SendAsync(HarviaClient.StartMessage(ids[i], _endpoint, token, _query, _receivers[i]), lifetime.Token).ConfigureAwait(false);
                            acknowledged();
                            _logger.LogDebug("MyHarvia's {Service} push is connected.", _service);
                            break;
                        case "data":
                            if (HarviaClient.ParseUpdate(message?["payload"]?["data"]) is HarviaUpdate update)
                                _onUpdate(update);
                            break;
                        case "error":
                        case "connection_error":
                            var errors = message?["payload"]?["errors"] as JsonArray;
                            var reason = string.Join("; ", errors?.OfType<JsonObject>().Select(e => HarviaJson.Text(e["message"]) ?? HarviaJson.Text(e["errorType"])) ?? []);
                            if (errors?.OfType<JsonObject>().Any(e => HarviaJson.Text(e["errorType"])?.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase) == true) == true)
                                throw new HarviaPushRejectedException(reason);
                            throw new InvalidOperationException($"MyHarvia's {_service} push answered an error: {reason}");
                        case "complete":
                            return false;
                    }
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                await StopAsync(socket, ids).ConfigureAwait(false);
                return true;
            }
        }

        // Ends the subscriptions politely before a planned reconnect; a failure doesn't matter.
        private async Task StopAsync(IHarviaSocket socket, List<string> ids)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                foreach (var id in ids)
                    await socket.SendAsync(new JsonObject { ["id"] = id, ["type"] = "stop" }.ToJsonString(), timeout.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is WebSocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
            {
                _logger.LogDebug("Could not stop MyHarvia's {Service} subscriptions: {Reason}", _service, error.Message);
            }
        }
    }
}
