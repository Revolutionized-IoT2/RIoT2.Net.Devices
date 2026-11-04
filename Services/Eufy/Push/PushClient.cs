// Ported from eufy-sdk src/transport/push/push-client.ts (PushClient) @ 5f92bea5 (Apache-2.0), modified for RIoT2.

using System.Globalization;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;

namespace RIoT2.Net.Devices.Services.Eufy.Push
{
    /// <summary>What the MCS client reports to its owner.</summary>
    internal interface IMcsPushHandler
    {
        /// <summary>A login succeeded (first time and after every reconnect).</summary>
        Task OnConnectedAsync(CancellationToken cancellationToken);
        /// <summary>A logged-in connection closed.</summary>
        void OnDisconnected();
        /// <summary>A data message arrived and was normalised.</summary>
        Task OnPushAsync(PushEvent push, CancellationToken cancellationToken);
        /// <summary>A connection error after the first login (the client reconnects on its own).</summary>
        void OnError(Exception error);
    }

    /// <summary>The MCS server rejected the login.</summary>
    internal sealed class McsLoginException(string message) : IOException(message);

    /// <summary>
    /// FCM/MCS push client: holds a TLS connection to Google's MCS (mtalk.google.com:5228), logs in with the
    /// check-in androidId/securityToken, heartbeats, and normalises DataMessageStanza pushes.
    /// </summary>
    internal sealed class McsPushClient
    {
        public const string Host = "mtalk.google.com";
        public const int Port = 5228;
        public const byte McsVersion = 41;
        public static readonly TimeSpan DefaultHeartbeatInterval = TimeSpan.FromMinutes(5);

        /// <summary>Consecutive MCS login rejections tolerated (self-healing propagation) before surfacing an error.</summary>
        private const int MaxLoginFailures = 3;

        private readonly FcmCredentials _creds;
        private readonly Func<CancellationToken, Task<Stream>> _connect;
        private readonly IMcsPushHandler _handler;
        private readonly ILogger _logger;
        private readonly Func<TimeSpan, CancellationToken, Task> _delay;
        private readonly TimeSpan _heartbeatInterval;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly Lock _idsLock = new();
        private List<string> _persistentIds = [];
        /// <summary>How many persistent ids the last LoginRequest reported; a successful login acknowledges them.</summary>
        private int _reportedIds;
        private int _loginFailures;
        private TimeSpan _currentDelay = TimeSpan.Zero;
        private bool _everConnected;
        private volatile bool _loggedIn;

        public McsPushClient(
            FcmCredentials creds,
            Func<CancellationToken, Task<Stream>> connect,
            IMcsPushHandler handler,
            ILogger logger,
            Func<TimeSpan, CancellationToken, Task>? delay = null,
            TimeSpan? heartbeatInterval = null)
        {
            _creds = creds;
            _connect = connect;
            _handler = handler;
            _logger = logger;
            _delay = delay ?? Task.Delay;
            _heartbeatInterval = heartbeatInterval ?? DefaultHeartbeatInterval;
        }

        public bool IsLoggedIn
        {
            get => _loggedIn;
            private set => _loggedIn = value;
        }

        /// <summary>Persistent ids already seen (set from storage to avoid re-delivery).</summary>
        public void SetPersistentIds(IEnumerable<string> ids)
        {
            lock (_idsLock) _persistentIds = [.. ids];
        }

        public IReadOnlyList<string> GetPersistentIds()
        {
            lock (_idsLock) return [.. _persistentIds];
        }

        /// <summary>
        /// Connects, logs in and keeps reconnecting with backoff (5 s, then +10 s up to 60 s, then +60 s up to
        /// 10 min; reset by a successful login) until <paramref name="stop"/>. A connection error or a
        /// persistent login rejection before the first login is thrown (upstream's <c>startPush</c> rejects on
        /// the client's first <c>error</c>); after it, errors go to <see cref="IMcsPushHandler.OnError"/>.
        /// </summary>
        public async Task RunAsync(CancellationToken stop)
        {
            while (true)
            {
                stop.ThrowIfCancellationRequested();
                Exception? error = null;
                try
                {
                    await RunConnectionAsync(stop).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    error = ex;
                }
                if (error is not null)
                {
                    if (!_everConnected) ExceptionDispatchInfo.Throw(error);
                    _logger.LogWarning("[push] socket error: {Error}", error.Message);
                    _handler.OnError(error);
                }
                await _delay(NextReconnectDelay(), stop).ConfigureAwait(false);
            }
        }

        internal TimeSpan NextReconnectDelay()
        {
            var delay = _currentDelay == TimeSpan.Zero ? TimeSpan.FromSeconds(5) : _currentDelay;
            if (_currentDelay < TimeSpan.FromSeconds(60)) _currentDelay += TimeSpan.FromSeconds(10);
            else if (_currentDelay < TimeSpan.FromSeconds(600)) _currentDelay += TimeSpan.FromSeconds(60);
            return delay;
        }

        private async Task RunConnectionAsync(CancellationToken stop)
        {
            IsLoggedIn = false;
            var stream = await _connect(stop).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                using var session = CancellationTokenSource.CreateLinkedTokenSource(stop);
                var parser = new McsParser();
                Task? heartbeat = null;
                Exception? heartbeatError = null;
                try
                {
                    _logger.LogDebug("[push] TLS connected, sending login");
                    await WriteAsync(stream, BuildLoginRequest(), stop).ConfigureAwait(false);
                    var buffer = new byte[8192];
                    while (true)
                    {
                        int read;
                        try
                        {
                            read = await stream.ReadAsync(buffer, session.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (!stop.IsCancellationRequested && heartbeatError is not null)
                        {
                            throw new IOException("MCS heartbeat write failed", heartbeatError);
                        }
                        if (read == 0) return;
                        foreach (var message in parser.HandleData(buffer.AsSpan(0, read)))
                        {
                            switch (await OnMessageAsync(stream, message, stop).ConfigureAwait(false))
                            {
                                case MessageOutcome.Close:
                                    return;
                                case MessageOutcome.LoggedIn:
                                    heartbeat ??= HeartbeatLoopAsync(stream, session, e => heartbeatError = e);
                                    break;
                            }
                        }
                    }
                }
                finally
                {
                    await session.CancelAsync().ConfigureAwait(false);
                    if (heartbeat is not null) await heartbeat.ConfigureAwait(false);
                    OnClose();
                }
            }
        }

        private enum MessageOutcome
        {
            Continue,
            LoggedIn,
            Close,
        }

        private async Task<MessageOutcome> OnMessageAsync(Stream stream, McsMessage m, CancellationToken stop)
        {
            switch ((McsMessageTag)m.Tag)
            {
                case McsMessageTag.LoginResponse:
                    var response = m.Message as McsLoginResponse;
                    if (response?.Error is { } error)
                    {
                        OnLoginError(error);
                        return MessageOutcome.Close;
                    }
                    IsLoggedIn = true;
                    _everConnected = true;
                    _currentDelay = TimeSpan.Zero;
                    _loginFailures = 0;
                    lock (_idsLock)
                    {
                        _persistentIds = _persistentIds.Skip(_reportedIds).ToList();
                        _reportedIds = 0;
                    }
                    _logger.LogDebug("[push] logged in");
                    await _handler.OnConnectedAsync(stop).ConfigureAwait(false);
                    return MessageOutcome.LoggedIn;
                case McsMessageTag.DataMessageStanza:
                    if (m.Message is McsDataMessageStanza stanza) await HandleDataMessageAsync(stanza, stop).ConfigureAwait(false);
                    return MessageOutcome.Continue;
                case McsMessageTag.HeartbeatPing:
                    var last = (m.Message as McsHeartbeat)?.LastStreamIdReceived;
                    await WriteAsync(stream, BuildHeartbeatAck(last), stop).ConfigureAwait(false);
                    return MessageOutcome.Continue;
                case McsMessageTag.Close:
                    _logger.LogDebug("[push] server sent Close");
                    return MessageOutcome.Close;
                default:
                    return MessageOutcome.Continue;
            }
        }

        /// <summary>
        /// Google occasionally rejects the first login right after check-in (<c>wrong_secret</c>) while the new
        /// credentials propagate, and the next attempt succeeds; so a rejection closes the connection to let the
        /// backoff reconnect retry. Only once it persists for <see cref="MaxLoginFailures"/> attempts is it an error.
        /// </summary>
        private void OnLoginError(McsErrorInfo error)
        {
            _loginFailures++;
            var message = $"MCS login error: {error}";
            if (_loginFailures >= MaxLoginFailures) throw new McsLoginException($"{message} (after {_loginFailures} attempts)");
            _logger.LogWarning("[push] {Error} - retrying (attempt {Attempt})", message, _loginFailures);
        }

        private async Task HandleDataMessageAsync(McsDataMessageStanza stanza, CancellationToken stop)
        {
            if (!string.IsNullOrEmpty(stanza.PersistentId))
            {
                lock (_idsLock) _persistentIds.Add(stanza.PersistentId);
            }
            var raw = PushNormalizer.ToRawMessage(stanza);
            var push = PushNormalizer.Normalize(raw);
            await _handler.OnPushAsync(push, stop).ConfigureAwait(false);
        }

        private Task HeartbeatLoopAsync(Stream stream, CancellationTokenSource session, Action<Exception> onFailure) => Task.Run(async () =>
        {
            try
            {
                using var timer = new PeriodicTimer(_heartbeatInterval);
                while (await timer.WaitForNextTickAsync(session.Token).ConfigureAwait(false))
                {
                    if (IsLoggedIn) await WriteAsync(stream, BuildHeartbeatPing(), session.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (session.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                onFailure(ex);
                await session.CancelAsync().ConfigureAwait(false);
            }
        }, CancellationToken.None);

        private void OnClose()
        {
            if (!IsLoggedIn) return;
            IsLoggedIn = false;
            _handler.OnDisconnected();
        }

        private async Task WriteAsync(Stream stream, byte[] frame, CancellationToken cancellationToken)
        {
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        internal byte[] BuildLoginRequest()
        {
            var hexAndroidId = ulong.Parse(_creds.AndroidId, NumberStyles.None, CultureInfo.InvariantCulture).ToString("x", CultureInfo.InvariantCulture);
            var request = new McsLoginRequest
            {
                AdaptiveHeartbeat = false,
                AuthService = McsLoginRequest.AuthServiceAndroidId,
                AuthToken = _creds.SecurityToken,
                Id = "chrome-63.0.3234.0",
                Domain = "mcs.android.com",
                DeviceId = $"android-{hexAndroidId}",
                NetworkType = 1,
                Resource = _creds.AndroidId,
                User = _creds.AndroidId,
                UseRmq2 = true,
            };
            request.Setting.Add(new McsSetting("new_vc", "1"));
            lock (_idsLock)
            {
                request.ReceivedPersistentId.AddRange(_persistentIds);
                _reportedIds = _persistentIds.Count;
            }
            return Frame([McsVersion, (byte)McsMessageTag.LoginRequest], request.Encode());
        }

        internal static byte[] BuildHeartbeatPing() => Frame([(byte)McsMessageTag.HeartbeatPing], new McsHeartbeat().Encode());

        internal static byte[] BuildHeartbeatAck(int? lastStreamId) =>
            Frame([(byte)McsMessageTag.HeartbeatAck], new McsHeartbeat { LastStreamIdReceived = lastStreamId is 0 ? null : lastStreamId }.Encode());

        /// <summary>The prefix bytes, then the message length-delimited (protobufjs <c>encodeDelimited</c>).</summary>
        internal static byte[] Frame(ReadOnlySpan<byte> prefix, byte[] body)
        {
            var w = new ProtoWriter();
            w.WriteRawBytes(prefix);
            w.WriteVarint((ulong)body.Length);
            w.WriteRawBytes(body);
            return w.ToArray();
        }
    }
}
