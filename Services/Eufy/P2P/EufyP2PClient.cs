// Ported from eufy-sdk src/transport/p2p/command-router.ts (openSession, makeSession, cipherKeysFor, resolveSession,
// sendBySessionLevel, replayLevel2Send, sendSetPayloadEnvelope, sendStationScalar, the snapshot media provider),
// src/transport/p2p/session-manager.ts and the RTC routing of src/client/eufy-mega.ts @ 5f92bea5 (Apache-2.0),
// modified for RIoT2.

using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace RIoT2.Net.Devices.Services.Eufy.P2P
{
    /// <summary>Timings and hooks of <see cref="EufyP2PClient"/>; the defaults are upstream's.</summary>
    internal sealed class EufyP2PClientSettings
    {
        public IP2PUdpTransport Transport { get; init; } = SocketP2PUdpTransport.Instance;
        public TimeProvider Time { get; init; } = TimeProvider.System;
        /// <summary>The session's own connect deadline (upstream <c>CONNECT_TIMEOUT_MS</c>).</summary>
        public TimeSpan ConnectTimeout { get; init; } = P2PSession.ConnectTimeout;
        /// <summary>How long a command that requires level 2 waits for the key, per attempt (it is re-prompted once).</summary>
        public TimeSpan Level2Grace { get; init; } = TimeSpan.FromSeconds(25);
        /// <summary>How long, from connect, a caller picking its seal once waits for the negotiation to settle.</summary>
        public TimeSpan Level2Settle { get; init; } = TimeSpan.FromSeconds(8);
        /// <summary>How many times an unacknowledged control frame is replayed, and the gap between replays.</summary>
        public int DirectCommandSends { get; init; } = 5;
        public TimeSpan DirectCommandInterval { get; init; } = TimeSpan.FromMilliseconds(200);
        /// <summary>
        /// How long, after the replays, a guard-mode write listens for the station's result. Upstream acknowledges the
        /// write once its frames are out; a result that arrives is read (a refusal throws), silence is not an error.
        /// </summary>
        public TimeSpan ResultGrace { get; init; } = TimeSpan.FromSeconds(3);
        public TimeSpan SnapshotTimeout { get; init; } = TimeSpan.FromSeconds(30);
        public TimeSpan SnapshotFallbackAfter { get; init; } = TimeSpan.FromSeconds(12);
        /// <summary>How long a battery station's idle session stays open (upstream <c>BATTERY_IDLE_MS</c>); a wired station's stays open.</summary>
        public TimeSpan BatteryIdle { get; init; } = TimeSpan.FromMinutes(5);
        public bool NoBroadcast { get; init; }
        /// <summary>Replaces the ffmpeg decode (tests).</summary>
        public Func<byte[], CancellationToken, Task<byte[]>>? SnapshotDecoder { get; init; }
    }

    /// <summary>
    /// The station P2P channel: one PPCS session per station (opened on demand, reused, closed when idle), the
    /// guard-mode write and the live snapshot. Calls on one station are serialized. HomeBase S1 Pro (T9000) and T8N00
    /// stations and the C30/C31 call doorbells are served over WebRTC upstream and are refused here.
    /// </summary>
    public sealed class EufyP2PClient : IEufyP2PClient
    {
        /// <summary>The station types that are mains-powered hubs; their sessions stay open while idle.</summary>
        private static readonly HashSet<int> WiredStationTypes = [0, 18, 25, 27, 28, 300];

        private sealed class StationSlot
        {
            public SemaphoreSlim Lock { get; } = new(1, 1);
            public P2PSession? Session;
            public ITimer? Idle;
        }

        private sealed record ResolvedTarget(EufyDeviceRecord Device, string ParentSn, bool HomeBaseAttached, string AccountId);

        private readonly EufyAccountOptions options;
        private readonly IEufyCloudApi cloud;
        private readonly ILogger logger;
        private readonly EufyP2PClientSettings settings;
        private readonly ConcurrentDictionary<string, StationSlot> slots = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<int, P2PCipherKeys> cipherKeyCache = new();
        private IReadOnlyList<EufyDeviceRecord> devices = [];
        private volatile bool disposed;

        public EufyP2PClient(EufyAccountOptions options, IEufyCloudApi cloud, ILogger logger)
            : this(options, cloud, logger, new EufyP2PClientSettings())
        {
        }

        internal EufyP2PClient(EufyAccountOptions options, IEufyCloudApi cloud, ILogger logger, EufyP2PClientSettings settings)
        {
            this.options = options;
            this.cloud = cloud;
            this.logger = logger;
            this.settings = settings;
        }

        /// <inheritdoc />
        public void UpdateDevices(IReadOnlyList<EufyDeviceRecord> devices) => Volatile.Write(ref this.devices, devices.ToArray());

        private IReadOnlyList<EufyDeviceRecord> Devices => Volatile.Read(ref devices);

        private EufyDeviceRecord? RecordFor(string sn) => Devices.FirstOrDefault(d => d.Serial == sn);

        private static int? DeviceTypeOf(EufyDeviceRecord dev) =>
            dev.DeviceType ?? (EufyRaw.Number(dev.Raw, "device_type") is { } t ? (int)t : null);

        /// <inheritdoc />
        public async Task SetGuardModeAsync(string stationSerial, int wireMode, CancellationToken cancellationToken)
        {
            if (!Arming.IsValidWireMode(wireMode))
                throw new ArgumentOutOfRangeException(nameof(wireMode), wireMode, "guard mode must be one of 0/1/2/3/4/5/6/47/63");
            ObjectDisposedException.ThrowIf(disposed, this);
            var dev = RecordFor(stationSerial) ?? throw new InvalidOperationException($"station {stationSerial} is not in the device list");
            var parentSn = StationChannels.StationOf(dev);
            EnsureP2PRoute(dev, parentSn, media: false);
            var context = new ArmingContext(dev.Model, StationChannels.StatedChannel(dev) ?? 0, cloud.AccountName, DeviceTypeOf(dev), dev.Serial);
            var command = Arming.BuildCommand(wireMode, context);
            var mode = Arming.ModeName(wireMode);
            var target = Resolve(dev, parentSn);

            await RunOnStationAsync(parentSn, async (slot, session) =>
            {
                var result = await DispatchAwaitingResultAsync(target, session, command, $"guard mode {mode} ({wireMode})", cancellationToken).ConfigureAwait(false);
                if (result is { } answered) logger.LogInformation("[p2p] {Station} guard mode set to {Mode} (result {Code})", parentSn, mode, answered.Code);
                else logger.LogInformation("[p2p] {Station} guard mode {Mode} sent (no result reported over P2P)", parentSn, mode);
                // Upstream resets the station's session after a guard-mode write: a following mode write needs a fresh one.
                await CloseSlotSessionAsync(slot, session).ConfigureAwait(false);
                return true;
            }, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<byte[]> SnapshotAsync(string deviceSerial, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var dev = RecordFor(deviceSerial) ?? throw new InvalidOperationException($"device {deviceSerial} is not in the device list");
            var parentSn = StationChannels.StationOf(dev);
            EnsureP2PRoute(dev, parentSn, media: true);
            var address = StationChannels.Resolve(Devices).GetValueOrDefault(deviceSerial);
            if (address.Channel is not { } channel)
                throw new InvalidOperationException($"device {deviceSerial} has no unambiguous channel on station {parentSn} ({address.Issue ?? "missing"})");
            var executable = Ffmpeg.Executable(options.FfmpegPath);
            if (settings.SnapshotDecoder is null && Ffmpeg.Locate(executable) is null) throw Ffmpeg.NotFound(executable);
            var target = Resolve(dev, parentSn);

            using var timeout = new CancellationTokenSource(settings.SnapshotTimeout, settings.Time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            try
            {
                return await RunOnStationAsync(parentSn, async (_, session) =>
                {
                    if (target.HomeBaseAttached) await RequireLevel2Async(session, parentSn, Level2Sender.Every, linked.Token).ConfigureAwait(false);
                    var streamOptions = new LiveStreamOptions
                    {
                        Channel = channel,
                        AccountId = target.AccountId,
                        HomeBaseAttached = target.HomeBaseAttached,
                        Time = settings.Time,
                    };
                    var snapshotOptions = new LiveSnapshotOptions
                    {
                        FfmpegExecutable = executable,
                        FallbackAfter = settings.SnapshotFallbackAfter,
                        Decode = settings.SnapshotDecoder,
                        Time = settings.Time,
                    };
                    var jpeg = await LiveSnapshot.CaptureAsync(session, streamOptions, snapshotOptions, logger, linked.Token).ConfigureAwait(false);
                    logger.LogDebug("[p2p] {Device} live snapshot captured ({Bytes} bytes)", deviceSerial, jpeg.Length);
                    return jpeg;
                }, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"live snapshot of {deviceSerial} did not complete within {settings.SnapshotTimeout.TotalSeconds:0}s");
            }
        }

        /// <summary>
        /// Refuses a device the vendor app drives over WebRTC (upstream <c>rtcRouteFor</c>): a T9000 or T8N00 station and
        /// every device on it, and a C30/C31 call doorbell that is its own station.
        /// </summary>
        private void EnsureP2PRoute(EufyDeviceRecord dev, string parentSn, bool media)
        {
            if (WebRtcRoute(Devices, dev.Serial) is not { } reason) return;
            var what = media ? "live snapshot" : "guard mode";
            throw new NotSupportedException($"{what} of {dev.Serial} is not supported: {reason}, which eufy drives over WebRTC, not P2P (station {parentSn})");
        }

        /// <summary>
        /// Why <paramref name="sn"/> rides eufy's WebRTC route rather than P2P, or null when it rides P2P. The station is
        /// the device's <c>station_sn</c> (else itself) and its family is read from its <c>device_type</c> and model.
        /// </summary>
        internal static string? WebRtcRoute(IReadOnlyList<EufyDeviceRecord> devices, string sn)
        {
            var target = devices.FirstOrDefault(d => d.Serial == sn);
            if (target is null) return null;
            var stationSn = string.IsNullOrEmpty(target.StationSerial) ? sn : target.StationSerial;
            var station = devices.FirstOrDefault(d => d.Serial == stationSn);
            if (station is null) return null;
            var family = new FamilyContext(EufyRaw.Number(station.Raw, "device_type") is { } t ? (int)t : null, station.Model);
            if (DeviceFamily.IsStation9000(family)) return "the station is a HomeBase S1 Pro (T9000)";
            if (DeviceFamily.IsNvrT8N00(family)) return "the station is a T8N00 NVR";
            if (DeviceFamily.IsCallDoorbell(family) && stationSn == sn) return $"the {station.Model} call doorbell";
            return null;
        }

        private ResolvedTarget Resolve(EufyDeviceRecord dev, string parentSn)
        {
            var admin = EufyRaw.Property(dev.Raw, "member") is { ValueKind: JsonValueKind.Object } m && EufyRaw.Property(m, "admin_user_id") is { ValueKind: JsonValueKind.String } a
                ? a.GetString()
                : null;
            return new ResolvedTarget(dev, parentSn, parentSn != dev.Serial, admin ?? cloud.UserId ?? "");
        }

        private async Task<T> RunOnStationAsync<T>(string parentSn, Func<StationSlot, P2PSession, Task<T>> work, CancellationToken cancellationToken)
        {
            var slot = slots.GetOrAdd(parentSn, _ => new StationSlot());
            await slot.Lock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                slot.Idle?.Dispose();
                slot.Idle = null;
                var session = await AcquireSessionAsync(slot, parentSn, cancellationToken).ConfigureAwait(false);
                return await work(slot, session).ConfigureAwait(false);
            }
            finally
            {
                ArmIdle(slot, parentSn);
                slot.Lock.Release();
            }
        }

        /// <summary>Opens the station's session, or reuses it while its path answers; waits for it to connect.</summary>
        private async Task<P2PSession> AcquireSessionAsync(StationSlot slot, string parentSn, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var session = slot.Session;
            if (session is not null && session.IsClosed)
            {
                Interlocked.CompareExchange(ref slot.Session, null, session);
                session = null;
            }
            if (session is not null && !session.PathAnswering)
            {
                logger.LogDebug("[p2p] {Station} path stopped answering — rebuilding before use", parentSn);
                await CloseSlotSessionAsync(slot, session).ConfigureAwait(false);
                session = null;
            }
            if (session is null)
            {
                session = await OpenSessionAsync(parentSn, cancellationToken).ConfigureAwait(false);
                var opened = session;
                session.Closed += () => Interlocked.CompareExchange(ref slot.Session, null, opened);
                slot.Session = session;
                await session.ConnectAsync(cancellationToken).ConfigureAwait(false);
            }
            try
            {
                await session.WaitConnectedAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (EufyStationUnreachableException)
            {
                Interlocked.CompareExchange(ref slot.Session, null, session);
                throw;
            }
            return session;
        }

        private async Task<P2PSession> OpenSessionAsync(string parentSn, CancellationToken cancellationToken)
        {
            var station = RecordFor(parentSn) ?? throw new InvalidOperationException($"station {parentSn} is not in the device list");
            var raw = station.Raw;
            var did = !string.IsNullOrEmpty(station.P2pDid) ? station.P2pDid : EufyRaw.String(raw, "p2p_did");
            if (string.IsNullOrEmpty(did)) throw new InvalidOperationException($"no P2P endpoint (p2p_did) for station {parentSn}");
            string? dskKey = null;
            try
            {
                var keys = await cloud.GetDskKeysAsync([parentSn], cancellationToken).ConfigureAwait(false);
                if (keys.TryGetValue(parentSn, out var k) && !string.IsNullOrEmpty(k.DskKey)) dskKey = k.DskKey;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning("[p2p] {Station} DSK key unavailable, the cloud lookup is skipped: {Message}", parentSn, e.Message);
            }
            var conn = EufyRaw.String(raw, "p2p_conn") ?? EufyRaw.String(raw, "app_conn");
            var adminUserId = EufyRaw.AdminUserId(raw) is { Length: > 0 } admin ? admin : cloud.UserId ?? "";
            var session = new P2PSession(new P2PSessionConfig
            {
                StationSn = parentSn,
                P2pDid = did,
                CloudAddresses = string.IsNullOrEmpty(conn) ? null : P2PCodec.DecodeP2PCloudIPs(conn),
                LocalAddress = LanIp.FreshestLanIp(raw),
                DskKey = dskKey,
                NoBroadcast = settings.NoBroadcast,
                ResolveCipherKey = (cipherId, ct) => CipherKeysForAsync(cipherId, adminUserId, parentSn, ct),
                Logger = logger,
                Transport = settings.Transport,
                Time = settings.Time,
                ConnectTimeout = settings.ConnectTimeout,
            });
            session.Error += e => logger.LogWarning("[p2p] {Station} {Message}", parentSn, e.Message);
            session.Level2Ready += cipherId => logger.LogDebug("[p2p] {Station} level-2 ready (cipher_id {CipherId})", parentSn, cipherId);
            return session;
        }

        /// <summary>The cipher keys for a <c>cipher_id</c> (<c>/v3/app/cipher/get_ciphers</c>), cached per client.</summary>
        private async Task<P2PCipherKeys?> CipherKeysForAsync(int cipherId, string userId, string stationSn, CancellationToken cancellationToken)
        {
            if (cipherKeyCache.TryGetValue(cipherId, out var cached)) return cached;
            JsonElement? chosen = null;
            try
            {
                var data = await cloud.SecurityAppPostAsync("/v3/app/cipher/get_ciphers", new
                {
                    cipher_ids = new[] { cipherId },
                    user_id = userId,
                    station_sn = stationSn,
                }, cancellationToken).ConfigureAwait(false);
                var list = data.ValueKind == JsonValueKind.Array ? data
                    : EufyRaw.Property(data, "ciphers") is { ValueKind: JsonValueKind.Array } c ? c
                    : EufyRaw.Property(data, "data") is { ValueKind: JsonValueKind.Array } d ? d
                    : default;
                if (list.ValueKind == JsonValueKind.Array)
                {
                    var entries = list.EnumerateArray().ToList();
                    chosen = entries.FirstOrDefault(e => CipherIdOf(e) == cipherId) is { ValueKind: JsonValueKind.Object } match ? match : null;
                    if (EufyRaw.String(chosen ?? default, "ecc_private_key") is null && entries.Count > 0 && EufyRaw.String(entries[0], "ecc_private_key") is not null)
                    {
                        logger.LogDebug("[p2p] {Station} get_ciphers answered cipher {Answered} for {Asked}; using it", stationSn, CipherIdOf(entries[0]), cipherId);
                        chosen = entries[0];
                    }
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning("[p2p] {Station} get_ciphers failed for cipher {CipherId}: {Message}", stationSn, cipherId, e.Message);
            }
            if (chosen is not { } entry) return null;
            var ecc = EufyRaw.String(entry, "ecc_private_key");
            var rsa = EufyRaw.String(entry, "private_key");
            if (ecc is null && rsa is null) return null;
            var keys = new P2PCipherKeys(ecc, rsa);
            cipherKeyCache[cipherId] = keys;
            return keys;
        }

        private static int? CipherIdOf(JsonElement e) => EufyRaw.Property(e, "cipher_id") switch
        {
            { ValueKind: JsonValueKind.Number } n when n.TryGetInt32(out var i) => i,
            { ValueKind: JsonValueKind.String } s when int.TryParse(s.GetString(), out var i) => i,
            _ => null,
        };

        private async Task RequireLevel2Async(P2PSession session, string parentSn, Level2Sender sender, CancellationToken cancellationToken)
        {
            var ready = await session.AwaitLevel2KeyAsync(settings.Level2Grace, false, sender, cancellationToken).ConfigureAwait(false);
            if (!ready && session.RepromptLevel2Key())
                ready = await session.AwaitLevel2KeyAsync(settings.Level2Grace, false, sender, cancellationToken).ConfigureAwait(false);
            if (!ready) throw new EufyStationKeyUnavailableException(parentSn);
        }

        /// <summary>
        /// Sends a write intent the way the router does (replayed <see cref="EufyP2PClientSettings.DirectCommandSends"/>
        /// times, the level picked by the session for <c>"auto"</c>). Like upstream the write is acknowledged once its
        /// frames are out; the station's result on the outer command, when it reports one within
        /// <see cref="EufyP2PClientSettings.ResultGrace"/>, is returned, and a non-zero one throws.
        /// </summary>
        private async Task<P2PCommandResult?> DispatchAwaitingResultAsync(ResolvedTarget target, P2PSession session, EufyP2PCommand command, string describe, CancellationToken cancellationToken)
        {
            var outerCmd = command is StationScalarCommand ? command.Cmd : P2PEnvelope.SetPayload;
            var result = new TaskCompletionSource<P2PCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnResult(P2PCommandResult r)
            {
                if (r.CommandId == outerCmd) result.TrySetResult(r);
            }
            session.CommandResult += OnResult;
            try
            {
                await DispatchAsync(target, session, command, describe, cancellationToken).ConfigureAwait(false);
                P2PCommandResult answer;
                try
                {
                    answer = await result.Task.WaitAsync(settings.ResultGrace, settings.Time, cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    logger.LogDebug("[p2p] {Station} reported no result for {Command} within {Grace}", target.ParentSn, describe, settings.ResultGrace);
                    return null;
                }
                if (answer.Code != 0)
                    throw new EufyP2PCommandException($"station {target.ParentSn} refused {describe} with result {answer.Code}", answer.Code);
                return answer;
            }
            finally
            {
                session.CommandResult -= OnResult;
            }
        }

        private async Task DispatchAsync(ResolvedTarget target, P2PSession session, EufyP2PCommand command, string describe, CancellationToken cancellationToken)
        {
            switch (command)
            {
                case SetPayloadCommand sp when sp.Form == "auto":
                    await session.AwaitLevel2KeyAsync(settings.Level2Settle, graceFromSession: true, Level2Sender.Every, cancellationToken).ConfigureAwait(false);
                    if (session.HasLevel2Key)
                    {
                        await ReplayAsync(target, describe, () => session.SendControlLevel2(sp.Cmd, sp.Channel, target.AccountId, sp.Payload, sp.MValue3 ?? sp.Cmd), cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        for (var i = 0; i < settings.DirectCommandSends; i++)
                        {
                            try
                            {
                                session.SendSetPayload(sp.Cmd, sp.Payload, target.AccountId, sp.Channel);
                            }
                            catch (InvalidOperationException e)
                            {
                                throw new EufyStationUnreachableException(target.ParentSn, $"{describe} for {target.Device.Serial} could not be sent: the P2P session to station {target.ParentSn} is no longer connected", e);
                            }
                            await Task.Delay(settings.DirectCommandInterval, settings.Time, cancellationToken).ConfigureAwait(false);
                        }
                    }
                    break;
                case SetPayloadCommand sp:
                    await RequireLevel2Async(session, target.ParentSn, Level2Sender.Every, cancellationToken).ConfigureAwait(false);
                    await ReplayAsync(target, describe, () => session.SendControlLevel2(sp.Cmd, sp.Channel, target.AccountId, sp.Payload, sp.MValue3 ?? sp.Cmd), cancellationToken).ConfigureAwait(false);
                    break;
                case StationScalarCommand sc:
                    if (string.IsNullOrEmpty(target.AccountId))
                        throw new InvalidOperationException($"{describe} for {target.Device.Serial} requires an account id (station member.admin_user_id or the logged-in user)");
                    await RequireLevel2Async(session, target.ParentSn, Level2Sender.Every, cancellationToken).ConfigureAwait(false);
                    var body = P2PWriteCommands.BuildDirectBinaryBody(sc.Value, target.AccountId);
                    await ReplayAsync(target, describe, () => session.SendRawLevel2Bytes(body, sc.Channel, sc.Cmd, 8), cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    throw new NotSupportedException($"command {command.GetType().Name} is not routable over P2P");
            }
        }

        /// <summary>Sends a level-2 frame <see cref="EufyP2PClientSettings.DirectCommandSends"/> times; fails when none could be sent.</summary>
        private async Task ReplayAsync(ResolvedTarget target, string describe, Func<bool> send, CancellationToken cancellationToken)
        {
            var sent = false;
            for (var i = 0; i < settings.DirectCommandSends; i++)
            {
                if (send()) sent = true;
                await Task.Delay(settings.DirectCommandInterval, settings.Time, cancellationToken).ConfigureAwait(false);
            }
            if (!sent) throw new EufyP2PException($"{describe} for {target.Device.Serial} was never sent (no level-2 key / session not connected)");
        }

        /// <summary>
        /// The session's power tier: a hub (by device type, or a station with attached devices) is wired and stays open;
        /// anything else is treated as battery-powered and closed after <see cref="EufyP2PClientSettings.BatteryIdle"/>.
        /// </summary>
        private bool IsWired(string parentSn)
        {
            var station = RecordFor(parentSn);
            if (station is null) return true;
            if (DeviceTypeOf(station) is { } type && WiredStationTypes.Contains(type)) return true;
            return Devices.Any(d => d.Serial != parentSn && StationChannels.StationOf(d) == parentSn);
        }

        private void ArmIdle(StationSlot slot, string parentSn)
        {
            slot.Idle?.Dispose();
            slot.Idle = null;
            if (slot.Session is null || disposed || IsWired(parentSn)) return;
            slot.Idle = settings.Time.CreateTimer(_ => _ = IdleCloseAsync(slot, parentSn), null, settings.BatteryIdle, Timeout.InfiniteTimeSpan);
        }

        private async Task IdleCloseAsync(StationSlot slot, string parentSn)
        {
            if (!await slot.Lock.WaitAsync(0).ConfigureAwait(false)) return;
            try
            {
                if (slot.Session is { } session)
                {
                    logger.LogDebug("[p2p] {Station} idle window elapsed — disconnecting so the device can sleep", parentSn);
                    await CloseSlotSessionAsync(slot, session).ConfigureAwait(false);
                }
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "[p2p] {Station} idle close failed", parentSn);
            }
            finally
            {
                slot.Lock.Release();
            }
        }

        private static async Task CloseSlotSessionAsync(StationSlot slot, P2PSession session)
        {
            Interlocked.CompareExchange(ref slot.Session, null, session);
            await session.CloseAsync().ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task CloseAsync()
        {
            var closing = new List<Task>();
            foreach (var (_, slot) in slots)
            {
                slot.Idle?.Dispose();
                slot.Idle = null;
                var session = Interlocked.Exchange(ref slot.Session, null);
                if (session is not null) closing.Add(session.CloseAsync());
            }
            await Task.WhenAll(closing).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (disposed) return;
            disposed = true;
            await CloseAsync().ConfigureAwait(false);
        }
    }
}
