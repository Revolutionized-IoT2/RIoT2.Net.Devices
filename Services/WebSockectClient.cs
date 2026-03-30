using System.Buffers;
using System.Net.WebSockets;
using System.Text;

namespace RIoT2.Net.Devices.Services
{
    public sealed class WebSocketClient : IAsyncDisposable
    {
        private ClientWebSocket _ws;
        private Task _listenTask;
        private CancellationTokenSource _listenCts;

        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly SemaphoreSlim _reconnectLock = new(1, 1);

        private volatile bool _userInitiatedClose;
        private volatile bool _reconnecting;

        // Reconnection state
        private Uri _cachedUri;
        private Dictionary<string, string> _cachedHeaders;
        private List<string> _cachedSubprotocols;

        // Pending response routing (text OR binary)
        private IPendingResponse _pendingResponse;
        private readonly object _pendingLock = new();

        // For reconnect decisions
        private WebSocketCloseStatus _lastCloseStatus;
        private string _lastCloseDescription;
        private Exception _lastListenerException;

        public event EventHandler<string> MessageReceived;
        public event EventHandler<byte[]> BinaryMessageReceived;
        public event EventHandler<Exception> ListenerFaulted;
        public event EventHandler Closed;
        public event EventHandler Reconnected;

        public Uri ConnectedUri { get; private set; }
        public bool IsConnected => _ws?.State == WebSocketState.Open;

        public AutoReconnectOptions AutoReconnect { get; } = new();

        /// <summary>
        /// Connect to a WebSocket endpoint.
        /// </summary>
        public async Task ConnectAsync(
            Uri uri,
            IDictionary<string, string> headers = null,
            IEnumerable<string> subprotocols = null,
            CancellationToken cancellationToken = default)
        {
            if (IsConnected)
                throw new InvalidOperationException("Already connected.");

            _userInitiatedClose = false;
            _cachedUri = uri;
            _cachedHeaders = headers != null ? new Dictionary<string, string>(headers) : null;
            _cachedSubprotocols = subprotocols != null ? new List<string>(subprotocols) : null;

            _ws = CreateClientWebSocket(_cachedHeaders, _cachedSubprotocols);
            await _ws.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);

            ConnectedUri = uri;

            // Start background listener
            _listenCts = new CancellationTokenSource();
            _listenTask = Task.Run(() => ListenLoopAsync(_listenCts.Token));
        }

        /// <summary>
        /// Disconnect gracefully and stop auto-reconnect.
        /// </summary>
        public async Task DisconnectAsync(
            WebSocketCloseStatus status = WebSocketCloseStatus.NormalClosure,
            string statusDescription = "Client disconnect",
            CancellationToken cancellationToken = default)
        {
            _userInitiatedClose = true;

            var ws = _ws;
            if (ws == null)
                return;

            try
            {
                if (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseReceived)
                {
                    await ws.CloseAsync(status, statusDescription, cancellationToken).ConfigureAwait(false);
                }
            }
            catch
            {
                // Best-effort close
            }
            finally
            {
                _listenCts?.Cancel();

                if (_listenTask != null)
                {
                    try { await _listenTask.ConfigureAwait(false); } catch { /* errors surfaced via ListenerFaulted */ }
                }

                _listenCts?.Dispose();
                _listenCts = null;

                ws.Dispose();
                _ws = null;
                ConnectedUri = null;

                // Fail any pending SendAndReceive so callers don't hang
                FailPendingResponse(new WebSocketException("Disconnected"), isTimeout: false);

                Closed?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Send a UTF-8 text message.
        /// </summary>
        public async Task SendAsync(string message, CancellationToken cancellationToken = default)
        {
            if (_ws == null || _ws.State != WebSocketState.Open)
                throw new InvalidOperationException("WebSocket is not connected.");

            var buffer = Encoding.UTF8.GetBytes(message);

            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _ws.SendAsync(new ArraySegment<byte>(buffer),
                                    WebSocketMessageType.Text,
                                    endOfMessage: true,
                                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        /// <summary>
        /// Send binary (single frame).
        /// </summary>
        public async Task SendBinaryAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            if (_ws == null || _ws.State != WebSocketState.Open)
                throw new InvalidOperationException("WebSocket is not connected.");

            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _ws.SendAsync(data,
                                    WebSocketMessageType.Binary,
                                    endOfMessage: true,
                                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        /// <summary>
        /// Send a text message and await the next text response.
        /// The paired response will NOT raise the MessageReceived event.
        /// </summary>
        public async Task<string> SendAndReceiveAsync(
            string message,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            if (_ws == null || _ws.State != WebSocketState.Open)
                throw new InvalidOperationException("WebSocket is not connected.");

            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = new PendingTextResponse(tcs);

            lock (_pendingLock)
            {
                if (_pendingResponse != null)
                    throw new InvalidOperationException("Another SendAndReceive is already in progress.");
                _pendingResponse = pending;
            }

            try
            {
                await SendAsync(message, cancellationToken).ConfigureAwait(false);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                if (timeout.HasValue) cts.CancelAfter(timeout.Value);

                try
                {
                    return await tcs.Task.WaitAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Distinguish timeout vs user cancellation
                    FailPendingIfMatch(pending, new TimeoutException("SendAndReceive timed out or was cancelled."), isTimeout: true);
                    throw;
                }
            }
            finally
            {
                ClearPendingIfMatch(pending);
            }
        }

        /// <summary>
        /// Send binary and await the next binary response.
        /// The paired response will NOT raise the BinaryMessageReceived event.
        /// </summary>
        public async Task<byte[]> SendAndReceiveBinaryAsync(
            ReadOnlyMemory<byte> data,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            if (_ws == null || _ws.State != WebSocketState.Open)
                throw new InvalidOperationException("WebSocket is not connected.");

            var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = new PendingBinaryResponse(tcs);

            lock (_pendingLock)
            {
                if (_pendingResponse != null)
                    throw new InvalidOperationException("Another SendAndReceive is already in progress.");
                _pendingResponse = pending;
            }

            try
            {
                await SendBinaryAsync(data, cancellationToken).ConfigureAwait(false);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                if (timeout.HasValue) cts.CancelAfter(timeout.Value);

                try
                {
                    return await tcs.Task.WaitAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    FailPendingIfMatch(pending, new TimeoutException("SendAndReceiveBinary timed out or was cancelled."), isTimeout: true);
                    throw;
                }
            }
            finally
            {
                ClearPendingIfMatch(pending);
            }
        }

        /// <summary>
        /// Main receive loop. Handles text & binary frames, fragmentation, and auto-reconnect.
        /// </summary>
        private async Task ListenLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var ws = _ws;
                    if (ws == null || ws.State != WebSocketState.Open)
                        return;

                    var buffer = ArrayPool<byte>.Shared.Rent(8 * 1024);
                    try
                    {
                        using var aggregator = new MemoryStream();

                        while (!cancellationToken.IsCancellationRequested && ws.State == WebSocketState.Open)
                        {
                            aggregator.SetLength(0);
                            WebSocketReceiveResult result;

                            do
                            {
                                var segment = new ArraySegment<byte>(buffer);
                                result = await ws.ReceiveAsync(segment, cancellationToken).ConfigureAwait(false);

                                if (result.MessageType == WebSocketMessageType.Close)
                                {
                                    _lastCloseStatus = result.CloseStatus.GetValueOrDefault();
                                    _lastCloseDescription = result.CloseStatusDescription;

                                    // Fail any pending SendAndReceive immediately
                                    FailPendingResponse(
                                        new WebSocketException($"Socket closed: {result.CloseStatus} {result.CloseStatusDescription}"),
                                        isTimeout: false);

                                    // Stop inner loop to trigger reconnect logic
                                    break;
                                }

                                if (result.Count > 0)
                                {
                                    aggregator.Write(buffer, 0, result.Count);
                                }

                            } while (!result!.EndOfMessage);

                            if (result!.MessageType == WebSocketMessageType.Close)
                            {
                                break; // break inner receive loop to reconnect branch
                            }

                            aggregator.Position = 0;

                            if (result.MessageType == WebSocketMessageType.Text)
                            {
                                using var sr = new StreamReader(aggregator, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                                var text = await sr.ReadToEndAsync().ConfigureAwait(false);

                                IPendingResponse toFulfill = null;
                                lock (_pendingLock) { toFulfill = _pendingResponse; }

                                if (toFulfill != null && toFulfill.TrySetText(text))
                                {
                                    // Consumed by SendAndReceive
                                    ClearPendingIfMatch(toFulfill);
                                }
                                else
                                {
                                    MessageReceived?.Invoke(this, text);
                                }
                            }
                            else if (result.MessageType == WebSocketMessageType.Binary)
                            {
                                var data = aggregator.ToArray();

                                IPendingResponse toFulfill = null;
                                lock (_pendingLock) { toFulfill = _pendingResponse; }

                                if (toFulfill != null && toFulfill.TrySetBinary(data))
                                {
                                    // Consumed by SendAndReceiveBinary
                                    ClearPendingIfMatch(toFulfill);
                                }
                                else
                                {
                                    BinaryMessageReceived?.Invoke(this, data);
                                }
                            }
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer, clearArray: false);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Normal on shutdown
                    return;
                }
                catch (Exception ex)
                {
                    _lastListenerException = ex;

                    // Fail any pending SendAndReceive immediately
                    FailPendingResponse(ex, isTimeout: false);

                    ListenerFaulted?.Invoke(this, ex);
                }

                // At this point, we have exited inner receive loop due to:
                // - Close frame, or
                // - Exception, or
                // - Cancellation
                if (cancellationToken.IsCancellationRequested)
                    return;

                if (_userInitiatedClose)
                {
                    // User asked to close; don't reconnect
                    Closed?.Invoke(this, EventArgs.Empty);
                    return;
                }

                // Decide if we should reconnect
                if (!(AutoReconnect?.Enabled ?? false))
                {
                    Closed?.Invoke(this, EventArgs.Empty);
                    return;
                }

                var should = AutoReconnect.ShouldReconnect?.Invoke(_lastCloseStatus, _lastCloseDescription, _lastListenerException) ?? true;
                if (!should)
                {
                    Closed?.Invoke(this, EventArgs.Empty);
                    return;
                }

                // Try to reconnect (blocks this loop until success or attempts exhausted)
                var reconnected = await TryReconnectLoopAsync(cancellationToken).ConfigureAwait(false);
                if (!reconnected)
                {
                    Closed?.Invoke(this, EventArgs.Empty);
                    return;
                }

                // Reconnected successfully — loop continues with new socket
                _lastCloseStatus = default;
                _lastCloseDescription = null;
                _lastListenerException = null;
            }
        }

        private async Task<bool> TryReconnectLoopAsync(CancellationToken cancellationToken)
        {
            await _reconnectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_reconnecting)
                    return true; // another loop succeeded

                _reconnecting = true;
            }
            finally
            {
                _reconnectLock.Release();
            }

            try
            {
                int attempt = 0;
                var delay = AutoReconnect.InitialDelay;
                var maxAttempts = AutoReconnect.MaxAttempts;

                while (!cancellationToken.IsCancellationRequested &&
                       !_userInitiatedClose &&
                       (_cachedUri != null))
                {
                    attempt++;

                    try
                    {
                        if (AutoReconnect.OnBeforeReconnectAsync != null)
                            await AutoReconnect.OnBeforeReconnectAsync(this).ConfigureAwait(false);

                        // Create a fresh socket
                        var newWs = CreateClientWebSocket(_cachedHeaders, _cachedSubprotocols);
                        await newWs.ConnectAsync(_cachedUri, cancellationToken).ConfigureAwait(false);

                        _ws?.Dispose();
                        _ws = newWs;
                        ConnectedUri = _cachedUri;

                        // Restart the listener (the current ListenLoop continues)
                        _listenCts?.Cancel();
                        _listenCts = new CancellationTokenSource();
                        _listenTask = Task.Run(() => ListenLoopAsync(_listenCts.Token));

                        if (AutoReconnect.OnAfterReconnectAsync != null)
                            await AutoReconnect.OnAfterReconnectAsync(this).ConfigureAwait(false);

                        Reconnected?.Invoke(this, EventArgs.Empty);
                        return true;
                    }
                    catch (OperationCanceledException)
                    {
                        return false;
                    }
                    catch
                    {
                        // Swallow and retry with backoff
                        if (maxAttempts.HasValue && attempt >= maxAttempts.Value)
                            return false;

                        var jittered = Jitter(delay, AutoReconnect.JitterFactor);
                        await Task.Delay(jittered, cancellationToken).ConfigureAwait(false);

                        delay = TimeSpan.FromMilliseconds(Math.Min(
                            AutoReconnect.MaxDelay.TotalMilliseconds,
                            delay.TotalMilliseconds * 2));
                    }
                }

                return false;
            }
            finally
            {
                _reconnecting = false;
            }
        }

        private static TimeSpan Jitter(TimeSpan d, double jitterFactor)
        {
            if (jitterFactor <= 0) return d;
            var rnd = Random.Shared.NextDouble(); // 0..1
            var factor = 1 - jitterFactor + (rnd * 2 * jitterFactor); // (1-j)..(1+j)
            var ms = Math.Max(1, d.TotalMilliseconds * factor);
            return TimeSpan.FromMilliseconds(ms);
        }

        private static ClientWebSocket CreateClientWebSocket(
            IDictionary<string, string> headers,
            IEnumerable<string> subprotocols)
        {
            var ws = new ClientWebSocket();
            if (headers != null)
            {
                foreach (var kv in headers)
                    ws.Options.SetRequestHeader(kv.Key, kv.Value);
            }
            if (subprotocols != null)
            {
                foreach (var sp in subprotocols)
                    ws.Options.AddSubProtocol(sp);
            }
            // Optionally tune:
            // ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            return ws;
        }

        private void FailPendingResponse(Exception ex, bool isTimeout)
        {
            IPendingResponse pending;
            lock (_pendingLock)
            {
                pending = _pendingResponse;
                _pendingResponse = null;
            }
            pending?.Fail(ex, isTimeout);
        }

        private void FailPendingIfMatch(IPendingResponse expected, Exception ex, bool isTimeout)
        {
            lock (_pendingLock)
            {
                if (ReferenceEquals(_pendingResponse, expected))
                {
                    _pendingResponse = null;
                    expected.Fail(ex, isTimeout);
                }
            }
        }

        private void ClearPendingIfMatch(IPendingResponse candidate)
        {
            lock (_pendingLock)
            {
                if (ReferenceEquals(_pendingResponse, candidate))
                    _pendingResponse = null;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await DisconnectAsync().ConfigureAwait(false);
            _sendLock.Dispose();
            _reconnectLock.Dispose();
        }

        // ----- Pending response helpers -----

        private interface IPendingResponse
        {
            bool TrySetText(string text);
            bool TrySetBinary(byte[] data);
            void Fail(Exception ex, bool isTimeout);
        }

        private sealed class PendingTextResponse : IPendingResponse
        {
            private readonly TaskCompletionSource<string> _tcs;
            public PendingTextResponse(TaskCompletionSource<string> tcs) => _tcs = tcs;

            public bool TrySetText(string text) => _tcs.TrySetResult(text);
            public bool TrySetBinary(byte[] data) => false;
            public void Fail(Exception ex, bool isTimeout)
            {
                _tcs.TrySetException(ex);
            }
        }

        private sealed class PendingBinaryResponse : IPendingResponse
        {
            private readonly TaskCompletionSource<byte[]> _tcs;
            public PendingBinaryResponse(TaskCompletionSource<byte[]> tcs) => _tcs = tcs;

            public bool TrySetText(string text) => false;
            public bool TrySetBinary(byte[] data) => _tcs.TrySetResult(data);
            public void Fail(Exception ex, bool isTimeout)
            {
                _tcs.TrySetException(ex);
            }
        }
    }

    public sealed class AutoReconnectOptions
    {
        /// <summary>Enable/disable auto-reconnect.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Initial backoff before first retry.</summary>
        public TimeSpan InitialDelay { get; set; } = TimeSpan.FromSeconds(1);

        /// <summary>Maximum backoff between retries.</summary>
        public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>Maximum attempts; null = infinite.</summary>
        public int? MaxAttempts { get; set; } = null;

        /// <summary>Randomization factor (0..1) applied to backoff (default 0.2).</summary>
        public double JitterFactor { get; set; } = 0.2;

        /// <summary>
        /// Optional predicate to decide if we should reconnect, given last close/exception.
        /// Return false to stop reconnecting.
        /// </summary>
        public Func<WebSocketCloseStatus?, string, Exception, bool> ShouldReconnect { get; set; }

        /// <summary>Optional hook before each reconnect attempt.</summary>
        public Func<WebSocketClient, Task> OnBeforeReconnectAsync { get; set; }

        /// <summary>Optional hook after a successful reconnect.</summary>
        public Func<WebSocketClient, Task> OnAfterReconnectAsync { get; set; }
    }

    internal static class TaskExtensions
    {
        /// <summary>
        /// Await with cancellation token (for frameworks prior to Task.WaitAsync).
        /// </summary>
        public static async Task<T> WaitAsync<T>(this Task<T> task, CancellationToken cancellationToken)
        {
            if (!cancellationToken.CanBeCanceled)
                return await task.ConfigureAwait(false);

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var reg = cancellationToken.Register(() => tcs.TrySetResult(true));
            if (task == await Task.WhenAny(task, tcs.Task).ConfigureAwait(false))
                return await task.ConfigureAwait(false);

            throw new OperationCanceledException(cancellationToken);
        }
    }
}
