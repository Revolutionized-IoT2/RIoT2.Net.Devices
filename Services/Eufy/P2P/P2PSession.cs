// Ported from eufy-sdk src/transport/p2p/p2p-session.ts @ 5f92bea5 (Apache-2.0), modified for RIoT2.
// Kept: lookup (LAN broadcast/unicast, cloud LOOKUP_WITH_KEY, punch probes, relay), CHECK_CAM/CAM_ID handshake,
// heartbeat, ACKs, sequence reordering and frame reassembly, level-1/level-2 (ECC GCM or RSA ECB) seals, the
// live-start retransmit and the media start/stop. Dropped: talkback/audio send, database and image reads.

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace RIoT2.Net.Devices.Services.Eufy.P2P
{
    /// <summary>Transport wiring for one station's PPCS session.</summary>
    internal sealed class P2PSessionConfig
    {
        public required string StationSn { get; init; }
        public required string P2pDid { get; init; }
        /// <summary>Cloud lookup servers (the decoded <c>p2p_conn</c>/<c>app_conn</c>).</summary>
        public IReadOnlyList<P2PAddress>? CloudAddresses { get; init; }
        /// <summary>The station's DSK key; with <see cref="CloudAddresses"/> it enables the cloud lookup.</summary>
        public string? DskKey { get; init; }
        /// <summary>A LAN address to send the local lookup to directly, besides the broadcast.</summary>
        public string? LocalAddress { get; init; }
        /// <summary>Skip the 255.255.255.255 broadcast lookup.</summary>
        public bool NoBroadcast { get; init; }
        /// <summary>Fetches the cipher keys for a <c>cipher_id</c>; enables level-2 negotiation.</summary>
        public Func<int, CancellationToken, Task<P2PCipherKeys?>>? ResolveCipherKey { get; init; }
        public ILogger Logger { get; init; } = NullLogger.Instance;
        public IP2PUdpTransport Transport { get; init; } = SocketP2PUdpTransport.Instance;
        public TimeProvider Time { get; init; } = TimeProvider.System;
        /// <summary>Overrides <see cref="P2PSession.ConnectTimeout"/> (tests).</summary>
        public TimeSpan? ConnectTimeout { get; init; }
    }

    /// <summary>One reassembled data frame from the station.</summary>
    internal sealed record P2PFrame(P2PDataFrameHeader Header, string StationSn, byte DataType, byte[] Data, byte[] Raw, JsonElement? Json)
    {
        public int CommandId => Header.CommandId;
        public int Channel => Header.Channel;
        public int SignCode => Header.SignCode;
    }

    /// <summary>A bodyless control reply: the numeric result of the command just sent.</summary>
    internal readonly record struct P2PCommandResult(int CommandId, int Code, int Channel);

    /// <summary>Who will use the level-2 key: every sender needs the GCM key, a set-payload envelope also takes an RSA cipher's ECB key.</summary>
    internal enum Level2Sender
    {
        Every,
        SetPayload
    }

    /// <summary>
    /// A eufy P2P (ThroughTek PPCS) UDP session to one station. All state is guarded by one lock; the receive loops
    /// and timers enter it, and the events are raised inside it, so handlers must not block.
    /// </summary>
    internal sealed class P2PSession
    {
        private const int LocalLookupPort = 32108;
        private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(5);
        /// <summary>Three heartbeats: one missed answer is a lost datagram, three is the path being gone.</summary>
        private static readonly TimeSpan PathSilence = Heartbeat * 3;
        private static readonly TimeSpan LookupRetry = TimeSpan.FromSeconds(1);
        /// <summary>Lookup rounds counted from the lookup server's acknowledgement before a relayed path is also asked for.</summary>
        private const int RelayAfterAckedRounds = 24;
        private const int RelayPacketCopies = 4;
        /// <summary>A keyframe arrives as one burst of datagrams; a large buffer keeps it from being dropped by the OS.</summary>
        private const int ReceiveBufferBytes = 4 * 1024 * 1024;
        /// <summary>The whole deadline for reaching a station: a connection that reaches it closes itself.</summary>
        public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(45);
        /// <summary>The channel a command addresses the station itself on.</summary>
        public const int StationChannel = 255;
        private const int StartLiveFrameTypeL1 = 11;
        private const int StartLiveFrameTypeL2 = 10;
        private static readonly TimeSpan LiveStartRetransmit = TimeSpan.FromMilliseconds(150);
        private static readonly TimeSpan LiveStartAckDeadline = TimeSpan.FromSeconds(3);
        private const int SequenceLookback = 0x8000;
        private const int StaleRetransmitDepth = 1024;
        private const int PunchProbeSockets = 7;
        private static readonly TimeSpan ReorderWait = TimeSpan.FromMilliseconds(250);
        private static readonly TimeSpan VideoReorderWait = TimeSpan.FromMilliseconds(700);
        private const int ReorderMaxDatagrams = 128;
        /// <summary>The one padded body read as a result code: a sign-code-0 SET_PAYLOAD answer of exactly this length.</summary>
        private const int PaddedResultBytes = 132;
        private static readonly byte[] GcmAad = "eufy security"u8.ToArray();
        /// <summary>The level-2 sub-header, counted as one little-endian uint32 from <c>[00, 03, 02, 01]</c>.</summary>
        private const uint Level2SeqBase = 0x01020300;

        internal static readonly JsonSerializerOptions WireJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private sealed class Retained
        {
            public required byte[] Data { get; init; }
            public required int Channel { get; init; }
            public long FirstSentAt { get; init; }
            public long SentAt { get; set; }
            public int Sends { get; set; }
        }

        private sealed class Pending
        {
            public P2PDataFrameHeader? Header { get; init; }
            public List<byte[]> Chunks { get; } = [];
            public int ByteLength { get; set; }
        }

        private sealed class Reorder
        {
            public Dictionary<int, byte[]> Held { get; } = [];
            public ITimer? Timer { get; set; }
        }

        private sealed class RelayState
        {
            public string Step { get; set; } = "list";
            public List<P2PAddress> Relays { get; } = [];
            public P2PAddress? Via { get; set; }
            public uint? Mark { get; set; }
        }

        private readonly P2PSessionConfig cfg;
        private readonly ILogger logger;
        private readonly TimeProvider time;
        private readonly object gate = new();
        private readonly byte[] level1Key;
        private readonly HashSet<IP2PUdpSocket> liveSockets = [];

        private IP2PUdpSocket? socket;
        private List<IP2PUdpSocket> probeSockets = [];
        private CancellationTokenSource? loopCts;
        private bool connected;
        private bool connecting;
        private bool closed;
        private bool receiveBufferReported;
        private P2PAddress? connectAddress;
        private int seqNumber;
        private byte[]? lastPongData;
        private long? lastPeerAt;
        private bool pathStaleTraced;
        private ITimer? lookupTimer;
        private ITimer? heartbeatTimer;
        private ITimer? connectTimer;
        private (string Key, IReadOnlyList<P2PAddress> Addresses)? cloudLookup;
        private int? ackedRounds;
        private RelayState? relay;
        private string? selfHost;
        private readonly Dictionary<int, Pending> pendingByDataType = [];
        private readonly Dictionary<int, int> lastSeqByType = [];
        private readonly Dictionary<int, Reorder> reorderByType = [];
        private byte[]? level2Key;
        private byte[]? level2EcbKey;
        private uint level2Seq;
        private RSA? rsa;
        private string? rsaModulusHex;
        private bool level2Negotiating;
        private bool level2Pending;
        private readonly List<TaskCompletionSource<string>> level2Waiters = [];
        private bool level2Reprompted;
        private long? connectedAtMs;
        private int connectionGeneration;
        private readonly Dictionary<int, string> liveStartedChannels = [];
        private int? gatewayCipherId;
        private int clearCommandCount;
        private readonly Dictionary<int, Retained> unackedLiveStarts = [];
        private ITimer? liveStartRetransmitTimer;
        private TaskCompletionSource connectedTcs = NewConnectedTcs();

        public P2PSession(P2PSessionConfig cfg)
        {
            this.cfg = cfg;
            logger = cfg.Logger;
            time = cfg.Time;
            level1Key = Encoding.ASCII.GetBytes(P2PCodec.P2pCommandEncryptionKey(cfg.StationSn, cfg.P2pDid));
            level2Pending = cfg.ResolveCipherKey is not null;
        }

        /// <summary>A reassembled data frame arrived.</summary>
        public event Action<P2PFrame>? FrameReceived;
        /// <summary>A bodyless control reply arrived: the result of a command.</summary>
        public event Action<P2PCommandResult>? CommandResult;
        public event Action? Connected;
        public event Action? Closed;
        public event Action<Exception>? Error;
        /// <summary>Video datagrams were lost: the access unit in flight is gone.</summary>
        public event Action? VideoGap;
        /// <summary>An own-session live start on this channel was never acknowledged.</summary>
        public event Action<int>? LiveStartUnacknowledged;
        public event Action<int>? Level2Ready;

        public string StationSn => cfg.StationSn;

        /// <summary>The session's lock; a consumer that calls back into the session from its own timers takes this one, so the two never invert.</summary>
        internal object SyncRoot => gate;

        private long Now => time.GetUtcNow().ToUnixTimeMilliseconds();

        private static TaskCompletionSource NewConnectedTcs() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsConnected
        {
            get { lock (gate) return connected; }
        }

        public bool IsClosed
        {
            get { lock (gate) return closed; }
        }

        public bool HasLevel2Key
        {
            get { lock (gate) return level2Key is not null; }
        }

        /// <summary>How long the connected path has gone without answering a heartbeat; null before it ever answered.</summary>
        public long? PathSilentMs
        {
            get { lock (gate) return lastPeerAt is null ? null : Now - lastPeerAt; }
        }

        /// <summary>False once the path has gone three heartbeats without an answer.</summary>
        public bool PathAnswering
        {
            get
            {
                lock (gate)
                {
                    var silent = lastPeerAt is null ? (long?)null : Now - lastPeerAt;
                    if (silent is null || silent < (long)PathSilence.TotalMilliseconds) return true;
                    if (!pathStaleTraced)
                    {
                        pathStaleTraced = true;
                        logger.LogDebug("[p2p] {Station} path silent for {Silent}ms — no heartbeat answer", cfg.StationSn, silent);
                    }
                    return false;
                }
            }
        }

        public void SetLevel2Key(byte[] key)
        {
            if (key.Length != 32) throw new ArgumentException($"level-2 key must be 32 bytes, got {key.Length}", nameof(key));
            lock (gate)
            {
                level2Key = key;
                SettleLevel2();
            }
        }

        private bool Level2KeyServes(Level2Sender sender) => level2Key is not null || (sender == Level2Sender.SetPayload && level2EcbKey is not null);

        /// <summary>
        /// Waits for the level-2 key within <paramref name="grace"/>, measured from this call or (with
        /// <paramref name="graceFromSession"/>) from connect. False when none arrives, the negotiation concludes
        /// without one, or the session closes.
        /// </summary>
        public async Task<bool> AwaitLevel2KeyAsync(TimeSpan grace, bool graceFromSession = false, Level2Sender sender = Level2Sender.Every, CancellationToken cancellationToken = default)
        {
            TaskCompletionSource<string> waiter;
            TimeSpan remaining;
            lock (gate)
            {
                if (closed) return false;
                if (Level2KeyServes(sender)) return true;
                if (!level2Pending)
                {
                    logger.LogDebug("[p2p] {Station} level-2 key unavailable: not negotiating", cfg.StationSn);
                    return false;
                }
                var since = graceFromSession ? (connectedAtMs ?? Now) : Now;
                remaining = grace - TimeSpan.FromMilliseconds(Now - since);
                if (remaining <= TimeSpan.Zero)
                {
                    logger.LogDebug("[p2p] {Station} no level-2 key and its {Grace} grace has elapsed", cfg.StationSn, grace);
                    return false;
                }
                logger.LogDebug("[p2p] {Station} waiting up to {Remaining} for the level-2 key", cfg.StationSn, remaining);
                waiter = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                level2Waiters.Add(waiter);
            }
            string outcome;
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                var delay = Task.Delay(remaining, time, cts.Token);
                var winner = await Task.WhenAny(waiter.Task, delay).ConfigureAwait(false);
                cts.Cancel();
                if (winner == waiter.Task) outcome = await waiter.Task.ConfigureAwait(false);
                else
                {
                    lock (gate) level2Waiters.Remove(waiter);
                    cancellationToken.ThrowIfCancellationRequested();
                    outcome = "timeout";
                }
            }
            lock (gate)
            {
                var served = outcome == "key" || (outcome == "terminal" && Level2KeyServes(sender));
                if (outcome == "timeout") logger.LogDebug("[p2p] {Station} level-2 key did not arrive within its grace", cfg.StationSn);
                else if (outcome == "terminal" && !served) logger.LogDebug("[p2p] {Station} level-2 negotiation concluded without a key", cfg.StationSn);
                else if (outcome == "closed") logger.LogDebug("[p2p] {Station} session closed before the level-2 key arrived", cfg.StationSn);
                return served;
            }
        }

        /// <summary>Asks the station once more for the gateway info that carries the level-2 key.</summary>
        public bool RepromptLevel2Key()
        {
            lock (gate)
            {
                if (closed || Level2KeyServes(Level2Sender.SetPayload) || level2Reprompted) return false;
                if (cfg.ResolveCipherKey is null || connectAddress is null) return false;
                level2Reprompted = true;
                level2Pending = true;
                logger.LogDebug("[p2p] {Station} asking again for the level-2 key", cfg.StationSn);
                SendCommand(P2PCommandType.GatewayInfo);
                return true;
            }
        }

        private void SettleLevel2(string reason = "terminal")
        {
            level2Pending = false;
            var outcome = level2Key is not null ? "key" : reason;
            var waiters = level2Waiters.ToArray();
            level2Waiters.Clear();
            foreach (var wake in waiters) wake.TrySetResult(outcome);
        }

        private void NegotiateLevel2Key(byte[] gwPayload)
        {
            level2Negotiating = true;
            var generation = connectionGeneration;
            var cipherId = P2PCodec.GatewayInfoCipherId(gwPayload);
            gatewayCipherId = cipherId;
            logger.LogDebug("[p2p] {Station} level-2 negotiating (cipher_id {CipherId})", cfg.StationSn, cipherId);
            var resolver = cfg.ResolveCipherKey!;
            _ = Task.Run(async () =>
            {
                try
                {
                    var keys = await resolver(cipherId, CancellationToken.None).ConfigureAwait(false);
                    var ecc = string.IsNullOrEmpty(keys?.Ecc) ? null : keys!.Ecc;
                    var rsaKey = ecc is null && !string.IsNullOrEmpty(keys?.Rsa) ? keys!.Rsa : null;
                    byte[]? key = null;
                    if (ecc is not null) key = P2PCodec.DeriveLevel2KeyFromGatewayInfo(gwPayload, ecc);
                    else if (rsaKey is not null) key = P2PCodec.DeriveLevel2KeyFromRsaGatewayInfo(gwPayload, rsaKey);
                    lock (gate)
                    {
                        if (closed || generation != connectionGeneration) return;
                        if (ecc is null && rsaKey is null)
                        {
                            logger.LogDebug("[p2p] {Station} no ECC or RSA key for cipher_id {CipherId}", cfg.StationSn, cipherId);
                            SettleLevel2();
                            return;
                        }
                        if (key is null)
                        {
                            logger.LogDebug("[p2p] {Station} level-2 key derivation failed (cipher_id {CipherId})", cfg.StationSn, cipherId);
                            SettleLevel2();
                            if (ecc is not null) Error?.Invoke(new EufyP2PException($"level-2 key derivation failed (cipher_id {cipherId})"));
                            return;
                        }
                        if (ecc is not null)
                        {
                            level2Key = key;
                            SettleLevel2();
                            logger.LogDebug("[p2p] {Station} level-2 key negotiated (cipher_id {CipherId})", cfg.StationSn, cipherId);
                            Level2Ready?.Invoke(cipherId);
                        }
                        else
                        {
                            level2EcbKey = key;
                            logger.LogDebug("[p2p] {Station} level-2 set-payload key negotiated (cipher_id {CipherId})", cfg.StationSn, cipherId);
                            SettleLevel2();
                        }
                    }
                }
                catch (Exception e)
                {
                    lock (gate)
                    {
                        if (closed || generation != connectionGeneration) return;
                        SettleLevel2();
                        Error?.Invoke(e);
                    }
                }
            });
        }

        /// <summary>
        /// Opens a level-2 frame body: <c>tag(16) ‖ nonce(12) ‖ [sub-header(4) on sign code 8] ‖ ct</c> under
        /// AES-256-GCM with the AAD "eufy security". Null when it does not authenticate.
        /// </summary>
        internal byte[]? DecryptLevel2(byte[] payload, int signCode)
        {
            var ctOffset = signCode == 8 ? 32 : 28;
            var key = level2Key;
            if (key is null || payload.Length < ctOffset) return null;
            try
            {
                using var gcm = new AesGcm(key, 16);
                var ct = payload.AsSpan(ctOffset);
                var plain = new byte[ct.Length];
                gcm.Decrypt(payload.AsSpan(16, 12), ct, payload.AsSpan(0, 16), plain, GcmAad);
                return plain;
            }
            catch (CryptographicException)
            {
                return null;
            }
        }

        /// <summary>Seals a level-2 body: <c>tag(16) ‖ nonce(12) ‖ sub-header(4) ‖ ct</c>.</summary>
        internal byte[]? EncryptLevel2(byte[] plaintext)
        {
            var key = level2Key;
            if (key is null) return null;
            var nonce = RandomNumberGenerator.GetBytes(12);
            var ct = new byte[plaintext.Length];
            var tag = new byte[16];
            using (var gcm = new AesGcm(key, 16)) gcm.Encrypt(nonce, plaintext, ct, tag, GcmAad);
            var sub = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(sub, unchecked(Level2SeqBase + level2Seq++));
            return [.. tag, .. nonce, .. sub, .. ct];
        }

        private void RequestReceiveBuffer(IP2PUdpSocket s)
        {
            var granted = s.RequestReceiveBuffer(ReceiveBufferBytes);
            if (granted >= ReceiveBufferBytes || receiveBufferReported) return;
            receiveBufferReported = true;
            var level = granted > 0 ? LogLevel.Debug : LogLevel.Warning;
            logger.Log(level, "[p2p] {Station} UDP receive buffer below the {Requested} bytes requested ({Granted}); live video can lose keyframes. Raise the OS limit (net.core.rmem_max on Linux).",
                cfg.StationSn, ReceiveBufferBytes, granted > 0 ? $"granted {granted}" : "request refused");
        }

        /// <summary>Starts connecting: binds, sends the lookups every second and gives up after the connect deadline.</summary>
        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            int generation;
            IP2PUdpSocket main;
            CancellationToken loops;
            lock (gate)
            {
                if (connecting || connected) return Task.CompletedTask;
                connecting = true;
                closed = false;
                connectionGeneration++;
                generation = connectionGeneration;
                ackedRounds = null;
                ResetInboundSequencing();
                if (!Level2KeyServes(Level2Sender.SetPayload))
                {
                    level2Pending = cfg.ResolveCipherKey is not null;
                    level2Negotiating = false;
                    level2Reprompted = false;
                    gatewayCipherId = null;
                    clearCommandCount = 0;
                }
                if (connectedTcs.Task.IsCompleted) connectedTcs = NewConnectedTcs();
                loopCts?.Dispose();
                loopCts = new CancellationTokenSource();
                loops = loopCts.Token;
                main = cfg.Transport.Bind(broadcast: true);
                socket = main;
                liveSockets.Add(main);
                RequestReceiveBuffer(main);
            }
            StartReceiveLoop(main, loops);
            _ = DetectSelfHostAsync(generation, loops);

            var dsk = cfg.DskKey;
            var cloud = cfg.CloudAddresses;
            lock (gate)
            {
                cloudLookup = !string.IsNullOrEmpty(dsk) && cloud is { Count: > 0 } ? (dsk, cloud) : null;
                if (cloudLookup is not null)
                {
                    _ = ResolveCloudAsync(generation, cloud!, loops);
                    StartPunchProbes(loops);
                }
                if (closed) return Task.CompletedTask;
                logger.LogDebug("[p2p] {Station} lookup channels: local={Local} cloud={Cloud}{Missing}", cfg.StationSn,
                    !cfg.NoBroadcast || cfg.LocalAddress is not null, cloudLookup is not null,
                    cloudLookup is null ? (string.IsNullOrEmpty(dsk) ? " (no DSK key)" : " (no cloud addresses)") : "");
                SendLookups();
                lookupTimer = time.CreateTimer(_ => { lock (gate) SendLookups(); }, null, LookupRetry, LookupRetry);
                connectTimer = time.CreateTimer(_ => OnConnectTimeout(generation), null, cfg.ConnectTimeout ?? ConnectTimeout, Timeout.InfiniteTimeSpan);
            }
            return Task.CompletedTask;
        }

        /// <summary>Waits for the handshake to complete; throws <see cref="EufyStationUnreachableException"/> when the session gives up or closes first.</summary>
        public Task WaitConnectedAsync(CancellationToken cancellationToken)
        {
            Task task;
            lock (gate)
            {
                if (connected) return Task.CompletedTask;
                if (closed && !connecting)
                {
                    // A session that gave up before connecting keeps the reason (e.g. the connect timeout).
                    if (connectedTcs.Task.IsFaulted) return connectedTcs.Task;
                    throw new EufyStationUnreachableException(cfg.StationSn, $"P2P session for station {cfg.StationSn} is closed");
                }
                task = connectedTcs.Task;
            }
            return task.WaitAsync(cancellationToken);
        }

        private void OnConnectTimeout(int generation)
        {
            EufyStationUnreachableException? error = null;
            lock (gate)
            {
                if (connected || closed || generation != connectionGeneration) return;
                error = new EufyStationUnreachableException(cfg.StationSn,
                    $"P2P connect timeout for station {cfg.StationSn} after {(cfg.ConnectTimeout ?? ConnectTimeout).TotalSeconds:0}s ({RelayProgress()})");
                Error?.Invoke(error);
            }
            _ = CloseAsync(error);
        }

        private async Task DetectSelfHostAsync(int generation, CancellationToken cancellationToken)
        {
            try
            {
                var host = await cfg.Transport.DetectLocalIpAsync(cancellationToken).ConfigureAwait(false);
                lock (gate) if (host is not null && !closed && generation == connectionGeneration) selfHost = host;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                logger.LogDebug(e, "[p2p] {Station} local IP probe failed", cfg.StationSn);
            }
        }

        private async Task ResolveCloudAsync(int generation, IReadOnlyList<P2PAddress> addresses, CancellationToken cancellationToken)
        {
            try
            {
                var resolved = await cfg.Transport.ResolveAsync(addresses, cancellationToken).ConfigureAwait(false);
                lock (gate)
                    if (cloudLookup is { } lookup && !closed && generation == connectionGeneration)
                        cloudLookup = (lookup.Key, resolved);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                logger.LogDebug(e, "[p2p] {Station} lookup server resolution failed", cfg.StationSn);
            }
        }

        private void StartReceiveLoop(IP2PUdpSocket s, CancellationToken cancellationToken) =>
            _ = Task.Run(() => ReceiveLoopAsync(s, cancellationToken), CancellationToken.None);

        private async Task ReceiveLoopAsync(IP2PUdpSocket s, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                P2PDatagram datagram;
                try
                {
                    datagram = await s.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    lock (gate) if (!liveSockets.Contains(s)) return;
                    if (cancellationToken.IsCancellationRequested) return;
                    logger.LogDebug(e, "[p2p] {Station} receive error", cfg.StationSn);
                    await Task.Delay(50, CancellationToken.None).ConfigureAwait(false);
                    continue;
                }
                lock (gate)
                {
                    if (!liveSockets.Contains(s)) return;
                    try
                    {
                        OnMessage(datagram.Data, datagram.Remote, s);
                    }
                    catch (Exception e)
                    {
                        logger.LogWarning(e, "[p2p] {Station} failed to handle a datagram", cfg.StationSn);
                    }
                }
            }
        }

        private void SendLookups()
        {
            if (connected || socket is null) return;
            var localPayload = P2PCodec.BuildLocalLookupPayload();
            if (!cfg.NoBroadcast) Send(new P2PAddress("255.255.255.255", LocalLookupPort), P2PMessageType.LocalLookup, localPayload);
            if (cfg.LocalAddress is not null) Send(new P2PAddress(cfg.LocalAddress, LocalLookupPort), P2PMessageType.LocalLookup, localPayload);
            if (cloudLookup is { } lookup)
            {
                SendCloudLookup(socket, lookup);
                foreach (var probe in probeSockets) SendCloudLookup(probe, lookup);
                logger.LogTrace("[p2p] {Station} sendLookups: cloud -> {Servers} self={Self}", cfg.StationSn, string.Join(", ", lookup.Addresses), selfHost ?? "unknown");
            }
            CountAckedRound();
            SendRelay();
        }

        private void CountAckedRound()
        {
            if (ackedRounds is null || relay is not null || cloudLookup is null) return;
            ackedRounds++;
            if (ackedRounds > RelayAfterAckedRounds) relay = new RelayState();
        }

        private string RelayProgress() => $"relay: {relay?.Step ?? (cloudLookup is not null ? "not started" : "no cloud lookup")}";

        private void SendRelay()
        {
            if (relay is not { } r || cloudLookup is not { } lookup || connected) return;
            switch (r.Step)
            {
                case "list":
                    {
                        var payload = P2PCodec.BuildLookupWithKeyPayload2(cfg.P2pDid, lookup.Key);
                        foreach (var addr in lookup.Addresses) Send(addr, P2PMessageType.LookupWithKey2, payload);
                        break;
                    }
                case "hello":
                    foreach (var addr in r.Relays) Send(addr, P2PMessageType.RelayHello);
                    break;
                case "port" when r.Via is { } via:
                    Send(via, P2PMessageType.RelayPort);
                    break;
                case "request" when r.Via is { } via && r.Mark is { } mark:
                    {
                        var payload = P2PCodec.BuildRelayRequestPayload(cfg.P2pDid, via, mark);
                        foreach (var addr in lookup.Addresses) Send(addr, P2PMessageType.RelayRequest, payload);
                        break;
                    }
            }
        }

        private void OnRelayMessage(byte[] msg, P2PAddress remote)
        {
            if (relay is not { } r || connected) return;
            if (P2PCodec.HasHeader(msg, P2PMessageType.ListReqAck))
            {
                if (r.Step != "list" && r.Step != "hello") return;
                foreach (var listed in P2PCodec.ParseRelayList(msg))
                    if (!r.Relays.Contains(listed)) r.Relays.Add(listed);
                if (r.Relays.Count > 0) r.Step = "hello";
            }
            else if (P2PCodec.HasHeader(msg, P2PMessageType.RelayHelloAck))
            {
                if (r.Step != "hello" || !r.Relays.Contains(remote)) return;
                r.Via = remote;
                r.Step = "port";
            }
            else if (P2PCodec.HasHeader(msg, P2PMessageType.RelayPortAck))
            {
                var ack = P2PCodec.ParseRelayPortAck(msg);
                if (r.Step != "port" || r.Via is not { } via || ack is not { } a) return;
                r.Via = new P2PAddress(via.Host, a.Port);
                r.Mark = a.Mark;
                r.Step = "request";
            }
            else
            {
                return;
            }
            logger.LogDebug("[p2p] {Station} relay -> {Step}", cfg.StationSn, r.Step);
            SendRelay();
        }

        private void SendCloudLookup(IP2PUdpSocket s, (string Key, IReadOnlyList<P2PAddress> Addresses) lookup)
        {
            var self = selfHost;
            var payload = self is not null
                ? P2PCodec.BuildLookupWithKeyPayload(cfg.P2pDid, self, s.LocalPort, lookup.Key)
                : P2PCodec.BuildLookupWithKeyPayload2(cfg.P2pDid, lookup.Key);
            var type = self is not null ? P2PMessageType.LookupWithKey : P2PMessageType.LookupWithKey2;
            foreach (var addr in lookup.Addresses) Send(addr, type, payload, s);
        }

        private void OnMessage(byte[] msg, P2PAddress remote, IP2PUdpSocket s)
        {
            var fromConnectedPeer = connected && connectAddress == remote;
            if (fromConnectedPeer && (P2PCodec.HasHeader(msg, P2PMessageType.Pong) || P2PCodec.HasHeader(msg, P2PMessageType.Ack)))
            {
                lastPeerAt = Now;
                pathStaleTraced = false;
            }
            if (!P2PCodec.HasHeader(msg, P2PMessageType.Data) && msg.Length >= 2)
                logger.LogTrace("[p2p] {Station} <<< {Remote} header={Header} len={Length}", cfg.StationSn, remote, Convert.ToHexString(msg, 0, 2), msg.Length);

            if (P2PCodec.HasHeader(msg, P2PMessageType.LocalLookupResp))
            {
                if (!connected) BeginCheckCam(remote, s);
            }
            else if (P2PCodec.HasHeader(msg, P2PMessageType.LookupAddr))
            {
                if (!connected && msg.Length >= 20)
                {
                    var addr = P2PCodec.ParseLookupAddr(msg);
                    logger.LogDebug("[p2p] {Station} LOOKUP_ADDR -> {Address}", cfg.StationSn, addr);
                    if (addr.Host != "0.0.0.0") BeginCheckCam(addr, s);
                }
            }
            else if (P2PCodec.HasHeader(msg, P2PMessageType.RelayTo))
            {
                var to = P2PCodec.ParseRelayTo(msg);
                if (!connected && to is { } t && t.Address.Host != "0.0.0.0")
                {
                    logger.LogDebug("[p2p] {Station} RLY_TO -> {Address}", cfg.StationSn, t.Address);
                    var payload = P2PCodec.BuildRelayPacketPayload(t.Handle, cfg.P2pDid);
                    for (var i = 0; i < RelayPacketCopies; i++) Send(t.Address, P2PMessageType.RelayPacket, payload, s);
                }
            }
            else if (P2PCodec.HasHeader(msg, P2PMessageType.RelayReady))
            {
                if (msg.Length >= 24 && msg.AsSpan(4, 20).SequenceEqual(P2PCodec.P2pDidToBuffer(cfg.P2pDid)))
                {
                    logger.LogDebug("[p2p] {Station} RLY_RDY from relay {Remote}", cfg.StationSn, remote);
                    OnConnected(remote, s);
                }
            }
            else if (P2PCodec.HasHeader(msg, P2PMessageType.P2PRequestAck))
            {
                if (!connected && ackedRounds is null && msg.Length > 4 && msg[4] == 0) ackedRounds = 0;
            }
            else if (P2PCodec.HasHeader(msg, P2PMessageType.ListReqAck) || P2PCodec.HasHeader(msg, P2PMessageType.RelayHelloAck)
                     || P2PCodec.HasHeader(msg, P2PMessageType.RelayPortAck) || P2PCodec.HasHeader(msg, P2PMessageType.RelayRequestAck))
            {
                if (s == socket) OnRelayMessage(msg, remote);
            }
            else if (P2PCodec.HasHeader(msg, P2PMessageType.CamId))
            {
                OnConnected(remote, s);
            }
            else if (P2PCodec.HasHeader(msg, P2PMessageType.End))
            {
                if (fromConnectedPeer) _ = CloseAsync(new EufyStationUnreachableException(cfg.StationSn, $"station {cfg.StationSn} ended the P2P session"));
            }
            else if (P2PCodec.HasHeader(msg, P2PMessageType.Pong))
            {
                lastPongData = msg.Length > 4 ? msg[4..] : null;
            }
            else if (P2PCodec.HasHeader(msg, P2PMessageType.Ping))
            {
                Send(remote, P2PMessageType.Pong, null, s);
            }
            else if (P2PCodec.HasHeader(msg, P2PMessageType.Ack))
            {
                OnAck(msg);
            }
            else if (P2PCodec.HasHeader(msg, P2PMessageType.Data))
            {
                if (connected && msg.Length >= 8) OnData(msg, remote);
            }
            else if (!P2PCodec.HasHeader(msg, P2PMessageType.CamAddr))
            {
                logger.LogTrace("[p2p] {Station} unhandled datagram of {Length} bytes", cfg.StationSn, msg.Length);
            }
        }

        private void BeginCheckCam(P2PAddress addr, IP2PUdpSocket s)
        {
            logger.LogDebug("[p2p] {Station} beginCheckCam -> {Address} (+/-3)", cfg.StationSn, addr);
            var payload = P2PCodec.BuildCheckCamPayload(cfg.P2pDid);
            Send(addr, P2PMessageType.CheckCam, payload, s);
            for (var p = addr.Port - 3; p <= addr.Port + 3; p++)
                if (p != addr.Port && p > 0) Send(new P2PAddress(addr.Host, p), P2PMessageType.CheckCam, payload, s);
        }

        private void StartPunchProbes(CancellationToken cancellationToken)
        {
            for (var i = 0; i < PunchProbeSockets; i++)
            {
                IP2PUdpSocket probe;
                try
                {
                    probe = cfg.Transport.Bind(broadcast: false);
                }
                catch (Exception e)
                {
                    logger.LogWarning("[p2p] lookup socket error: {Message}", e.Message);
                    continue;
                }
                probeSockets.Add(probe);
                liveSockets.Add(probe);
                StartReceiveLoop(probe, cancellationToken);
            }
        }

        private void StopPunchProbes(IP2PUdpSocket? winner = null)
        {
            foreach (var probe in probeSockets)
            {
                if (probe == winner) continue;
                liveSockets.Remove(probe);
                probe.Dispose();
            }
            probeSockets = [];
        }

        private void OnConnected(P2PAddress addr, IP2PUdpSocket s)
        {
            if (connected) return;
            StopPunchProbes(s);
            if (socket != s)
            {
                if (socket is not null)
                {
                    liveSockets.Remove(socket);
                    socket.Dispose();
                }
                socket = s;
                RequestReceiveBuffer(s);
            }
            connected = true;
            connectedAtMs = Now;
            connecting = false;
            connectAddress = addr;
            lookupTimer?.Dispose();
            lookupTimer = null;
            connectTimer?.Dispose();
            connectTimer = null;
            relay = null;
            logger.LogDebug("[p2p] {Station} connected {Address}", cfg.StationSn, addr);
            SendCommand(P2PCommandType.GatewayInfo);
            Send(addr, P2PMessageType.Ping, lastPongData);
            heartbeatTimer = time.CreateTimer(_ =>
            {
                lock (gate) if (connectAddress is { } a && connected) Send(a, P2PMessageType.Ping, lastPongData);
            }, null, Heartbeat, Heartbeat);
            connectedTcs.TrySetResult();
            Connected?.Invoke();
        }

        /// <summary>
        /// Starts (or keeps alive) live media on a channel. A HomeBase-attached camera takes the level-2 <c>1003</c>
        /// SET_PAYLOAD; an own-session camera takes the <c>1700</c> START_LIVE (then <c>1139</c> keepalives).
        /// </summary>
        public void StartLiveMedia(int channel, string accountId, bool homeBaseAttached, bool force = false)
        {
            lock (gate)
            {
                if (homeBaseAttached)
                {
                    logger.LogDebug("[p2p] {Station} media start (attached) on channel {Channel}, level2={Level2}", cfg.StationSn, channel, level2Key is not null);
                    SendMediaPayloadLevel2(P2PCommandType.StartRealtimeMedia, channel, accountId);
                    return;
                }
                var want = level2Key is not null ? "l2" : SendsInTheClear() ? "clear" : "l1";
                if (force)
                {
                    if (unackedLiveStarts.Values.Any(p => p.Channel == channel)) return;
                    liveStartedChannels.Remove(channel);
                }
                if (liveStartedChannels.TryGetValue(channel, out var started) && started == want)
                {
                    SendCommand(P2PCommandType.StreamKeepalive, channel);
                }
                else
                {
                    logger.LogDebug("[p2p] {Station} media start (own) on channel {Channel}, seal={Seal}", cfg.StationSn, channel, want);
                    SendStartLiveOwnSession(channel, accountId);
                    liveStartedChannels[channel] = want;
                }
            }
        }

        private byte[] StartLiveJson(int channel, string accountId, int? clearMsgId = null)
        {
            JsonObject json;
            if (clearMsgId is { } msgId)
            {
                json = new JsonObject
                {
                    ["commandType"] = P2PCommandType.StartLive,
                    ["data"] = new JsonObject { ["accountId"] = accountId, ["encryptkey"] = RsaModulus(), ["msg_id"] = msgId, ["streamtype"] = 1 },
                };
            }
            else
            {
                var now = Now;
                json = new JsonObject
                {
                    ["commandType"] = P2PCommandType.StartLive,
                    ["data"] = new JsonObject
                    {
                        ["cmd"] = P2PCommandType.StartLive,
                        ["account_id"] = accountId,
                        ["accountId"] = accountId,
                        ["mValueStrSub"] = accountId,
                        ["mChannel"] = channel,
                        ["mValue3"] = 0,
                        ["mValue5"] = 0,
                        ["msg_id"] = 1,
                        ["camera_type"] = 0,
                        ["entrytype"] = 0,
                        ["extValue"] = P2PCommandType.StartLive,
                        ["ivalue"] = 1,
                        ["restore"] = 0,
                        ["streamtype"] = 2,
                        ["video_type"] = 12,
                        ["timestamp"] = now,
                        ["transaction"] = now.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["encryptkey"] = RsaModulus(),
                    },
                };
            }
            return Encoding.UTF8.GetBytes(json.ToJsonString(WireJson));
        }

        private void SendStartLiveOwnSession(int channel, string accountId)
        {
            if (connectAddress is not { } addr) return;
            byte[] payload;
            int signCode, frameType;
            byte magic0, magic1;
            if (level2Key is not null)
            {
                var enc = EncryptLevel2(StartLiveJson(channel, accountId));
                if (enc is null) return;
                payload = enc;
                signCode = 8;
                (magic0, magic1) = (0x08, 0x00);
                frameType = StartLiveFrameTypeL2;
            }
            else if (SendsInTheClear())
            {
                (magic0, magic1) = NextClearCommandBytes();
                payload = StartLiveJson(channel, accountId, magic1);
                signCode = 0;
                frameType = 0;
            }
            else
            {
                payload = P2PCodec.EncryptP2PData(P2PCodec.PaddingP2PData(StartLiveJson(channel, accountId)), level1Key);
                signCode = 1;
                (magic0, magic1) = (0x01, 0x00);
                frameType = StartLiveFrameTypeL1;
            }
            var sequence = seqNumber;
            byte[] data = [.. P2PCodec.BuildCommandHeader(sequence, P2PCommandType.ControlPayload),
                .. P2PCodec.BuildRawCommandPayload(payload, channel, signCode, magic0, magic1, frameType)];
            seqNumber = (seqNumber + 1) & 0xffff;
            foreach (var key in unackedLiveStarts.Where(kv => kv.Value.Channel == channel).Select(kv => kv.Key).ToList())
                unackedLiveStarts.Remove(key);
            var sentAt = Now;
            unackedLiveStarts[sequence] = new Retained { Channel = channel, Data = data, FirstSentAt = sentAt, SentAt = sentAt, Sends = 1 };
            ArmLiveStartRetransmit();
            Send(addr, P2PMessageType.Data, data);
        }

        private bool SendsInTheClear() => gatewayCipherId == 0;

        private (byte, byte) NextClearCommandBytes()
        {
            clearCommandCount = (clearCommandCount % 0xff) + 1;
            return (0x01, (byte)clearCommandCount);
        }

        private void ArmLiveStartRetransmit()
        {
            if (liveStartRetransmitTimer is not null) return;
            var period = LiveStartRetransmit / 2;
            liveStartRetransmitTimer = time.CreateTimer(_ =>
            {
                lock (gate)
                {
                    if (liveStartRetransmitTimer is null) return;
                    var outstanding = RetransmitUnackedLiveStarts();
                    if (!outstanding) ClearLiveStartRetransmit();
                }
            }, null, period, period);
        }

        private bool RetransmitUnackedLiveStarts()
        {
            if (connectAddress is not { } addr) return false;
            var now = Now;
            foreach (var (sequence, held) in unackedLiveStarts.ToList())
            {
                if (now - held.SentAt < (long)LiveStartRetransmit.TotalMilliseconds) continue;
                if (now - held.FirstSentAt >= (long)LiveStartAckDeadline.TotalMilliseconds)
                {
                    unackedLiveStarts.Remove(sequence);
                    liveStartedChannels.Remove(held.Channel);
                    logger.LogDebug("[p2p] {Station} live start on channel {Channel} was never acknowledged", cfg.StationSn, held.Channel);
                    LiveStartUnacknowledged?.Invoke(held.Channel);
                    continue;
                }
                held.Sends++;
                held.SentAt = now;
                Send(addr, P2PMessageType.Data, held.Data);
            }
            return unackedLiveStarts.Count > 0;
        }

        private void ClearLiveStartRetransmit()
        {
            liveStartRetransmitTimer?.Dispose();
            liveStartRetransmitTimer = null;
        }

        /// <summary>Sends a level-1 <c>SET_PAYLOAD</c> envelope <c>{account_id,cmd,mChannel,mValue3:0,payload}</c>; throws when not connected.</summary>
        public void SendSetPayload(int subCmd, JsonNode? payload, string accountId, int channel)
        {
            lock (gate)
            {
                if (connectAddress is not { } addr) throw new InvalidOperationException($"P2P session {cfg.StationSn} is not connected");
                var inner = new JsonObject
                {
                    ["account_id"] = accountId,
                    ["cmd"] = subCmd,
                    ["mChannel"] = channel,
                    ["mValue3"] = 0,
                    ["payload"] = payload?.DeepClone() ?? new JsonObject(),
                };
                byte[] body = [.. P2PCodec.BuildCommandHeader(seqNumber, P2PCommandType.SetPayload),
                    .. P2PCodec.BuildStringCommandPayload(inner.ToJsonString(WireJson), channel, level1Key, 1)];
                seqNumber = (seqNumber + 1) & 0xffff;
                logger.LogDebug("[p2p] {Station} sendSetPayload subCmd={SubCmd}", cfg.StationSn, subCmd);
                Send(addr, P2PMessageType.Data, body);
            }
        }

        /// <summary>
        /// Sends a level-2 <c>SET_PAYLOAD</c> envelope: GCM sign code 8 under the ECC-negotiated key, else AES-128-ECB
        /// at encryption type 2 under an RSA cipher's key. False when neither key is held or the session is not connected.
        /// </summary>
        public bool SendControlLevel2(int cmd, int channel, string accountId, JsonNode? payload, int mValue3)
        {
            lock (gate)
            {
                if (connectAddress is not { } addr || !Level2KeyServes(Level2Sender.SetPayload)) return false;
                var value = Encoding.UTF8.GetBytes(new JsonObject
                {
                    ["account_id"] = accountId,
                    ["cmd"] = cmd,
                    ["mChannel"] = channel,
                    ["mValue3"] = mValue3,
                    ["payload"] = payload?.DeepClone() ?? new JsonObject(),
                }.ToJsonString(WireJson));
                byte[] sealedBody;
                if (level2Key is not null)
                {
                    var body = EncryptLevel2(value);
                    if (body is null) return false;
                    sealedBody = P2PCodec.BuildRawCommandPayload(body, channel, 8, 0x08, 0x00, 0);
                }
                else
                {
                    sealedBody = P2PCodec.BuildRawCommandPayload(P2PCodec.EncryptP2PData(P2PCodec.PaddingP2PData(value), level2EcbKey!), channel, 2);
                }
                byte[] data = [.. P2PCodec.BuildCommandHeader(seqNumber, P2PCommandType.SetPayload), .. sealedBody];
                seqNumber = (seqNumber + 1) & 0xffff;
                Send(addr, P2PMessageType.Data, data);
                return true;
            }
        }

        /// <summary>Sends <paramref name="payload"/> GCM-sealed as the body of outer command <paramref name="outerCmd"/>; false without the GCM key.</summary>
        public bool SendRawLevel2Bytes(byte[] payload, int channel, int outerCmd, int signCode = 8)
        {
            lock (gate)
            {
                if (connectAddress is not { } addr || level2Key is null) return false;
                var body = EncryptLevel2(payload);
                if (body is null) return false;
                byte[] data = [.. P2PCodec.BuildCommandHeader(seqNumber, outerCmd), .. P2PCodec.BuildRawCommandPayload(body, channel, signCode, 0x08, 0x00, 0)];
                seqNumber = (seqNumber + 1) & 0xffff;
                Send(addr, P2PMessageType.Data, data);
                return true;
            }
        }

        /// <summary>Stops live media on a channel, sealed the way the matching start was.</summary>
        public void StopLiveMedia(int channel, string accountId, bool homeBaseAttached)
        {
            lock (gate)
            {
                liveStartedChannels.Remove(channel);
                foreach (var key in unackedLiveStarts.Where(kv => kv.Value.Channel == channel).Select(kv => kv.Key).ToList())
                    unackedLiveStarts.Remove(key);
                if (unackedLiveStarts.Count == 0) ClearLiveStartRetransmit();
                var body = new byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(body, (uint)channel);
                if (level2Key is not null && !homeBaseAttached)
                {
                    SendRawLevel2Bytes(body, channel, P2PCommandType.StopRealtimeMedia, 8);
                }
                else if (level2Key is not null)
                {
                    SendMediaPayloadLevel2(P2PCommandType.StopRealtimeMedia, channel, accountId);
                }
                else if (!homeBaseAttached && SendsInTheClear() && connectAddress is { } addr)
                {
                    var (m0, m1) = NextClearCommandBytes();
                    byte[] data = [.. P2PCodec.BuildCommandHeader(seqNumber, P2PCommandType.StopRealtimeMedia), .. P2PCodec.BuildRawCommandPayload(body, channel, 0, m0, m1, 0)];
                    seqNumber = (seqNumber + 1) & 0xffff;
                    Send(addr, P2PMessageType.Data, data);
                }
                else
                {
                    SendCommand(P2PCommandType.StopRealtimeMedia, channel);
                }
            }
        }

        private void OnAck(byte[] msg)
        {
            if (msg.Length < 8 || msg[4] != 0xd1) return;
            var count = BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(6));
            if (msg[5] != P2PDataType.Data) return;
            for (var i = 0; i < count && 10 + 2 * i <= msg.Length; i++)
            {
                var sequence = BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(8 + 2 * i));
                if (unackedLiveStarts.Remove(sequence)) logger.LogDebug("[p2p] {Station} live start acknowledged", cfg.StationSn);
            }
            if (unackedLiveStarts.Count == 0) ClearLiveStartRetransmit();
        }

        private void SendMediaPayloadLevel2(int subCmd, int channel, string accountId)
        {
            if (connectAddress is not { } addr || level2Key is null)
            {
                logger.LogDebug("[p2p] {Station} media command {Cmd} unsent: {Reason}", cfg.StationSn, subCmd, connectAddress is null ? "no address" : "no level-2 key");
                return;
            }
            JsonNode inner = subCmd == P2PCommandType.StartRealtimeMedia
                ? new JsonObject { ["ClientOS"] = "Android", ["accountId"] = accountId, ["camera_type"] = 0, ["entrytype"] = 0, ["key"] = RsaModulus(), ["streamtype"] = 2 }
                : new JsonObject();
            var value = new JsonObject
            {
                ["account_id"] = accountId,
                ["cmd"] = subCmd,
                ["mChannel"] = channel,
                ["mValue3"] = subCmd,
                ["payload"] = inner,
            }.ToJsonString(WireJson);
            var body = EncryptLevel2(Encoding.UTF8.GetBytes(value));
            if (body is null) return;
            var streamId = channel == StationChannel || channel == 0 ? 0 : 0x0a + (int)(level2Seq & 0x7f);
            byte[] data = [.. P2PCodec.BuildCommandHeader(seqNumber, P2PCommandType.SetPayload), .. P2PCodec.BuildRawCommandPayload(body, channel, 8, 0x08, 0x00, streamId)];
            seqNumber = (seqNumber + 1) & 0xffff;
            Send(addr, P2PMessageType.Data, data);
        }

        /// <summary>
        /// The Annex-B payload of a <c>CMD_VIDEO_FRAME</c>: past the 22-byte header, the first 128 bytes AES-ECB
        /// decrypted under the key the camera RSA-wrapped for this session's modulus when the frame is sealed.
        /// </summary>
        public byte[]? DecodeVideoFrame(byte[] data, int signCode)
        {
            if (data.Length < 22) return null;
            var videoDataLength = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(data), int.MaxValue);
            var payloadStart = 22;
            byte[]? aesKey = null;
            if (signCode > 0 && videoDataLength >= 128)
            {
                RSA? key;
                lock (gate) key = rsa;
                if (key is null || data.Length < 150) return null;
                try
                {
                    aesKey = key.Decrypt(data.AsSpan(22, 128).ToArray(), RSAEncryptionPadding.Pkcs1);
                }
                catch (CryptographicException)
                {
                    return null;
                }
                payloadStart = 151;
            }
            var end = (int)Math.Min((long)payloadStart + videoDataLength, data.Length);
            if (aesKey is { Length: >= 16 })
            {
                var encEnd = Math.Min(payloadStart + 128, data.Length);
                if (payloadStart >= encEnd) return null;
                try
                {
                    using var aes = Aes.Create();
                    aes.Key = aesKey.Length >= 32 ? aesKey[..32] : aesKey[..16];
                    var head = aes.DecryptEcb(data.AsSpan(payloadStart, encEnd - payloadStart), PaddingMode.None);
                    var tail = encEnd < end ? data.AsSpan(encEnd, end - encEnd).ToArray() : [];
                    return [.. head, .. tail];
                }
                catch (CryptographicException)
                {
                    return null;
                }
            }
            return payloadStart < end ? data[payloadStart..end] : [];
        }

        private string RsaModulus()
        {
            if (rsaModulusHex is null)
            {
                rsa = RSA.Create(1024);
                var modulus = rsa.ExportParameters(false).Modulus!;
                rsaModulusHex = Convert.ToHexStringLower(modulus);
            }
            return rsaModulusHex;
        }

        private void SendCommand(int commandType, int channel = StationChannel)
        {
            if (connectAddress is not { } addr) return;
            byte[] data = [.. P2PCodec.BuildCommandHeader(seqNumber, commandType), .. P2PCodec.BuildVoidCommandPayload(channel)];
            seqNumber = (seqNumber + 1) & 0xffff;
            Send(addr, P2PMessageType.Data, data);
        }

        private void OnData(byte[] msg, P2PAddress addr)
        {
            var dataType = msg[5];
            var seqNo = BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(6));
            Send(addr, P2PMessageType.Ack, P2PCodec.BuildAckPayload(AckDataType(dataType), seqNo));

            var advance = lastSeqByType.TryGetValue(dataType, out var prevSeq) ? (seqNo - prevSeq) & 0xffff : 1;
            if (advance == 0) return;
            var body = msg[8..];
            if (advance > SequenceLookback)
            {
                if (0x10000 - advance <= StaleRetransmitDepth) return;
                ClearReorderTimer(dataType);
                reorderByType.Remove(dataType);
                pendingByDataType.Remove(dataType);
                if (dataType == P2PDataType.Video) VideoGap?.Invoke();
                lastSeqByType[dataType] = seqNo;
                Reassemble(dataType, body);
                return;
            }
            if (advance == 1)
            {
                lastSeqByType[dataType] = seqNo;
                Reassemble(dataType, body);
                DrainReorder(dataType);
                return;
            }
            HoldForRetransmit(dataType, seqNo, body);
        }

        private void HoldForRetransmit(int dataType, int seqNo, byte[] body)
        {
            if (!reorderByType.TryGetValue(dataType, out var reorder)) reorderByType[dataType] = reorder = new Reorder();
            reorder.Held.TryAdd(seqNo, body);
            if (reorder.Held.Count > ReorderMaxDatagrams)
            {
                AbandonHole(dataType);
                return;
            }
            ArmReorderTimer(dataType);
        }

        private void ArmReorderTimer(int dataType)
        {
            if (!reorderByType.TryGetValue(dataType, out var reorder) || reorder.Held.Count == 0 || reorder.Timer is not null) return;
            var generation = connectionGeneration;
            reorder.Timer = time.CreateTimer(_ =>
            {
                lock (gate)
                {
                    if (closed || generation != connectionGeneration || reorder.Timer is null) return;
                    if (!reorderByType.TryGetValue(dataType, out var current) || current != reorder) return;
                    reorder.Timer.Dispose();
                    reorder.Timer = null;
                    AbandonHole(dataType);
                }
            }, null, dataType == P2PDataType.Video ? VideoReorderWait : ReorderWait, Timeout.InfiniteTimeSpan);
        }

        private void DrainReorder(int dataType)
        {
            if (!reorderByType.TryGetValue(dataType, out var reorder) || reorder.Held.Count == 0) return;
            ClearReorderTimer(dataType);
            while (true)
            {
                var next = (lastSeqByType[dataType] + 1) & 0xffff;
                if (!reorder.Held.Remove(next, out var body)) break;
                lastSeqByType[dataType] = next;
                Reassemble(dataType, body);
            }
            if (reorder.Held.Count > 0) ArmReorderTimer(dataType);
            else reorderByType.Remove(dataType);
        }

        private void AbandonHole(int dataType)
        {
            var held = reorderByType[dataType].Held;
            logger.LogTrace("[p2p] {Station} datagram gap on data type {DataType}", cfg.StationSn, dataType);
            pendingByDataType.Remove(dataType);
            if (dataType == P2PDataType.Video) VideoGap?.Invoke();
            var last = lastSeqByType[dataType];
            var earliest = held.Keys.OrderBy(k => (k - last) & 0xffff).First();
            lastSeqByType[dataType] = (earliest - 1) & 0xffff;
            DrainReorder(dataType);
        }

        private void ClearReorderTimer(int dataType)
        {
            if (!reorderByType.TryGetValue(dataType, out var reorder) || reorder.Timer is null) return;
            reorder.Timer.Dispose();
            reorder.Timer = null;
        }

        private void Reassemble(int dataType, byte[] datagramBody)
        {
            pendingByDataType.Remove(dataType, out var pending);
            var body = pending is { Header: null } ? [.. pending.Chunks.SelectMany(c => c), .. datagramBody] : datagramBody;
            var offset = 0;

            if (pending?.Header is { } openHeader)
            {
                var taken = (int)Math.Min(openHeader.BytesToRead - (uint)pending.ByteLength, (uint)body.Length);
                pending.Chunks.Add(body[..taken]);
                pending.ByteLength += taken;
                offset = taken;
                if (pending.ByteLength < openHeader.BytesToRead)
                {
                    pendingByDataType[dataType] = pending;
                    return;
                }
                HandleFrame(openHeader, Concat(pending.Chunks, pending.ByteLength), (byte)dataType);
            }

            while (body.Length - offset >= P2PCodec.DataHeaderBytes && body.AsSpan(offset, 4).SequenceEqual(P2PCodec.MagicBytes))
            {
                var header = P2PCodec.ParseDataFrameHeader(body.AsSpan(offset));
                var payloadStart = offset + P2PCodec.DataHeaderBytes;
                var available = body.Length - payloadStart;
                if (available < header.BytesToRead)
                {
                    var open = new Pending { Header = header, ByteLength = available };
                    open.Chunks.Add(body[payloadStart..]);
                    pendingByDataType[dataType] = open;
                    return;
                }
                var length = (int)header.BytesToRead;
                HandleFrame(header, body.AsSpan(payloadStart, length).ToArray(), (byte)dataType);
                offset = payloadStart + length;
            }
            var rest = body.Length - offset;
            if (rest > 0 && rest < P2PCodec.DataHeaderBytes && P2PCodec.MagicBytes.AsSpan(0, Math.Min(4, rest)).SequenceEqual(body.AsSpan(offset, Math.Min(4, rest))))
            {
                var partial = new Pending { ByteLength = rest };
                partial.Chunks.Add(body[offset..]);
                pendingByDataType[dataType] = partial;
            }
        }

        private static byte[] Concat(List<byte[]> chunks, int length)
        {
            var output = new byte[length];
            var at = 0;
            foreach (var c in chunks)
            {
                var n = Math.Min(c.Length, length - at);
                Array.Copy(c, 0, output, at, n);
                at += n;
            }
            return output;
        }

        private void ResetInboundSequencing()
        {
            foreach (var dataType in reorderByType.Keys.ToList()) ClearReorderTimer(dataType);
            reorderByType.Clear();
            lastSeqByType.Clear();
            pendingByDataType.Clear();
        }

        private static byte AckDataType(byte dataType) => dataType is P2PDataType.Video or P2PDataType.Control or P2PDataType.Binary ? dataType : P2PDataType.Data;

        /// <summary>Decrypts, parses and dispatches one reassembled frame.</summary>
        internal void HandleFrame(P2PDataFrameHeader header, byte[] payload, byte dataType)
        {
            var data = payload;
            var isMedia = header.CommandId is P2PCommandType.VideoFrame or P2PCommandType.AudioFrame;
            if (isMedia)
            {
                // leave raw: the media path opens it
            }
            else if ((header.SignCode == 2 || header.SignCode == 8) && level2Key is not null)
            {
                data = DecryptLevel2(payload, header.SignCode) ?? data;
            }
            else if (header.SignCode > 0 && data.Length > 0 && data.Length % 16 == 0)
            {
                try
                {
                    data = P2PCodec.DecryptP2PData(data, level1Key);
                }
                catch (CryptographicException)
                {
                    // leave as-is
                }
            }

            if (header.CommandId == P2PCommandType.GatewayInfo && header.SignCode <= 1 && !Level2KeyServes(Level2Sender.SetPayload)
                && !level2Negotiating && cfg.ResolveCipherKey is not null)
            {
                NegotiateLevel2Key(data);
            }

            JsonElement? json = null;
            if (!isMedia)
            {
                var text = P2PCodec.ReadNullTerminatedString(data);
                if (text.StartsWith('{'))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(text);
                        json = doc.RootElement.Clone();
                    }
                    catch (JsonException)
                    {
                        // not JSON
                    }
                }
            }
            var frame = new P2PFrame(header, cfg.StationSn, dataType, data, payload, json);
            var paddedResult = header.CommandId == P2PCommandType.SetPayload && header.SignCode == 0 && data.Length == PaddedResultBytes;
            if (!isMedia && json is null && (data.Length == 4 || paddedResult))
                CommandResult?.Invoke(new P2PCommandResult(header.CommandId, BinaryPrimitives.ReadInt32LittleEndian(data), header.Channel));
            FrameReceived?.Invoke(frame);
        }

        private void Send(P2PAddress addr, ushort type, byte[]? payload = null, IP2PUdpSocket? via = null)
        {
            var s = via ?? socket;
            if (s is null) return;
            try
            {
                s.Send(P2PCodec.FrameMessage(type, payload), addr);
            }
            catch (Exception e) when (e is System.Net.Sockets.SocketException or ObjectDisposedException or FormatException)
            {
                logger.LogDebug("[p2p] send error {Address}: {Message}", addr, e.Message);
            }
        }

        /// <summary>Closes the session: sends END to a connected peer, releases the sockets and wakes every waiter.</summary>
        public Task CloseAsync(Exception? reason = null)
        {
            IP2PUdpSocket? s;
            lock (gate)
            {
                if (closed) return Task.CompletedTask;
                closed = true;
                StopPunchProbes();
                connectionGeneration++;
                level2Key = null;
                level2EcbKey = null;
                level2Seq = 0;
                SettleLevel2("closed");
                var wasConnected = connected;
                connected = false;
                connecting = false;
                lookupTimer?.Dispose();
                lookupTimer = null;
                heartbeatTimer?.Dispose();
                heartbeatTimer = null;
                connectTimer?.Dispose();
                connectTimer = null;
                relay = null;
                ClearLiveStartRetransmit();
                unackedLiveStarts.Clear();
                liveStartedChannels.Clear();
                ResetInboundSequencing();
                s = socket;
                if (s is not null && connectAddress is { } addr && wasConnected) Send(addr, P2PMessageType.End, null, s);
                if (s is not null) liveSockets.Remove(s);
                socket = null;
                connectAddress = null;
                loopCts?.Cancel();
                rsa?.Dispose();
                rsa = null;
                rsaModulusHex = null;
                connectedTcs.TrySetException(reason as EufyStationUnreachableException
                    ?? new EufyStationUnreachableException(cfg.StationSn, $"P2P session for station {cfg.StationSn} closed before it connected", reason));
            }
            s?.Dispose();
            Closed?.Invoke();
            return Task.CompletedTask;
        }
    }
}
