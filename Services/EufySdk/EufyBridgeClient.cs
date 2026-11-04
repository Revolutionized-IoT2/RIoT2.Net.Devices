using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace RIoT2.Net.Devices.Services.EufySdk
{
    /// <summary>eufy-sdk refused a bridge request; <see cref="ErrorName"/> is the JavaScript error's name.</summary>
    public sealed class EufySdkException : Exception
    {
        public EufySdkException(string message, string? errorName = null, Exception? inner = null) : base(message, inner)
        {
            ErrorName = errorName;
        }

        public string? ErrorName { get; }
    }

    /// <summary>
    /// The JSON lines protocol of the bridge (<c>EufySdkBridge/src/bridge.mjs</c>): requests with an id and their
    /// replies, and the events the bridge sends on its own.
    /// </summary>
    internal sealed class EufyBridgeClient : IAsyncDisposable
    {
        private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(2);
        private readonly IEufyBridgeConnection _connection;
        private readonly ILogger _logger;
        private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
        private readonly Task _reader;
        private long _nextId;
        private volatile bool _closed;

        public EufyBridgeClient(IEufyBridgeConnection connection, ILogger logger)
        {
            _connection = connection;
            _logger = logger;
            _reader = Task.Run(ReadAsync);
        }

        /// <summary>
        /// An event the bridge sent (name, data). Raised on the reader: don't call the bridge from the handler and
        /// wait for its reply there.
        /// </summary>
        public event Action<string, JsonElement>? EventReceived;

        /// <summary>Completes with the exit code when the bridge exits.</summary>
        public Task<int> Exited => _connection.Exited;

        public async Task<JsonElement> InvokeAsync(string method, object? parameters, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var id = Interlocked.Increment(ref _nextId);
            var reply = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = reply;
            try
            {
                var request = new JsonObject
                {
                    ["id"] = id,
                    ["method"] = method,
                    ["params"] = parameters == null ? new JsonObject() : JsonSerializer.SerializeToNode(parameters)
                };
                if (_closed)
                    throw new EufySdkException($"The eufy-sdk bridge has stopped; '{method}' was not sent.");
                await _connection.WriteLineAsync(request.ToJsonString(), cancellationToken).ConfigureAwait(false);
                return await reply.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException error)
            {
                throw new EufySdkException($"eufy-sdk did not answer '{method}' in {timeout.TotalSeconds:0} s.", inner: error);
            }
            catch (IOException error)
            {
                throw new EufySdkException($"The eufy-sdk bridge has stopped; '{method}' was not sent.", inner: error);
            }
            finally
            {
                _pending.TryRemove(id, out _);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
            await _reader.ConfigureAwait(false);
        }

        private async Task ReadAsync()
        {
            var output = _connection.Output;
            try
            {
                while (await output.WaitToReadAsync().ConfigureAwait(false))
                {
                    while (output.TryRead(out var line))
                        Handle(line);
                }
            }
            finally
            {
                _closed = true;
                var exitCode = "unknown";
                try
                {
                    exitCode = (await _connection.Exited.WaitAsync(ExitWait).ConfigureAwait(false)).ToString(CultureInfo.InvariantCulture);
                }
                catch (TimeoutException)
                {
                }

                foreach (var pending in _pending.Values)
                    pending.TrySetException(new EufySdkException($"The eufy-sdk bridge stopped (exit code {exitCode}) before it answered."));
            }
        }

        private void Handle(string line)
        {
            JsonElement message;
            try
            {
                using var document = JsonDocument.Parse(line);
                message = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                _logger.LogWarning("The eufy-sdk bridge wrote a line that is not JSON; ignored.");
                return;
            }

            if (message.ValueKind != JsonValueKind.Object)
                return;

            if (message.TryGetProperty("id", out var idElement) && idElement.TryGetInt64(out var id))
            {
                if (!_pending.TryGetValue(id, out var reply))
                    return;
                if (message.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                {
                    var text = error.TryGetProperty("message", out var m) ? m.GetString() : null;
                    var name = error.TryGetProperty("name", out var n) ? n.GetString() : null;
                    reply.TrySetException(new EufySdkException(text ?? "eufy-sdk failed.", name));
                }
                else
                {
                    reply.TrySetResult(message.TryGetProperty("result", out var result) ? result : default);
                }
            }
            else if (message.TryGetProperty("event", out var nameElement) && nameElement.GetString() is { } name)
            {
                var data = message.TryGetProperty("data", out var d) ? d : default;
                try
                {
                    EventReceived?.Invoke(name, data);
                }
                catch (Exception error)
                {
                    _logger.LogError(error, "Handling the eufy-sdk '{Event}' event failed.", name);
                }
            }
        }
    }
}
