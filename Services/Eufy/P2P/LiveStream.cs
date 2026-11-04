// Ported from eufy-sdk src/transport/p2p/live-stream.ts @ 5f92bea5 (Apache-2.0), modified for RIoT2.
// Video only: audio frames, talkback and the shared-source budget are not ported.

using Microsoft.Extensions.Logging;

namespace RIoT2.Net.Devices.Services.Eufy.P2P
{
    /// <summary>One complete video access unit from a live stream.</summary>
    internal sealed record LiveVideoFrame(uint SourceTimestamp, bool Keyframe, int Width, int Height, VideoCodec Codec, byte[] Data);

    internal sealed class LiveStreamOptions
    {
        /// <summary>The camera's channel within its station.</summary>
        public int Channel { get; init; } = P2PSession.StationChannel;
        public string AccountId { get; init; } = "";
        /// <summary>The camera hangs off a HomeBase whose session serves several channels.</summary>
        public bool HomeBaseAttached { get; init; }
        /// <summary>How often the start is re-sent (own session: 1139 keepalive; attached: the start, until media flows).</summary>
        public TimeSpan KeepAlive { get; init; } = TimeSpan.FromSeconds(3);
        /// <summary>How long an attached channel may stay silent before its start is re-asserted (default 2 × keepalive).</summary>
        public TimeSpan? Stall { get; init; }
        /// <summary>The cipher's ECC private key, for cameras whose frames are ECIES/GCM sealed.</summary>
        public byte[]? EccPrivateKey { get; init; }
        public TimeProvider Time { get; init; } = TimeProvider.System;
    }

    /// <summary>A live video pull from one camera over its station's session.</summary>
    internal sealed class LiveStream
    {
        private const int VideoHeaderLength = 0x16;

        private readonly P2PSession session;
        private readonly LiveStreamOptions opts;
        private readonly ILogger logger;
        private readonly object gate;
        private readonly AccessUnitAssembler units;
        private readonly VideoFrameDecoder? decoder;
        private readonly int? mediaChannel;
        private bool listening;
        private ITimer? kaTimer;
        private ITimer? stallTimer;
        private VideoCodec lastCodec = VideoCodec.H264;
        private bool awaitingKeyframeAfterGap;

        public LiveStream(P2PSession session, LiveStreamOptions opts, ILogger logger)
        {
            this.session = session;
            this.opts = opts;
            this.logger = logger;
            gate = session.SyncRoot;
            units = new AccessUnitAssembler(ReportDroppedUnit);
            if (opts.HomeBaseAttached) mediaChannel = opts.Channel;
            if (opts.EccPrivateKey is not null) decoder = new VideoFrameDecoder(opts.EccPrivateKey);
        }

        public event Action<LiveVideoFrame>? Video;
        public event Action? Unacknowledged;

        public LiveStream Start()
        {
            lock (gate)
            {
                if (listening) return this;
                listening = true;
                session.FrameReceived += OnFrame;
                session.VideoGap += OnVideoGap;
                session.LiveStartUnacknowledged += OnUnacknowledged;
                SendStart();
                if (opts.KeepAlive > TimeSpan.Zero)
                    kaTimer = opts.Time.CreateTimer(_ => { lock (gate) if (listening) SendStart(); }, null, opts.KeepAlive, opts.KeepAlive);
            }
            return this;
        }

        public void Stop()
        {
            lock (gate)
            {
                if (!listening) return;
                listening = false;
                session.FrameReceived -= OnFrame;
                session.VideoGap -= OnVideoGap;
                session.LiveStartUnacknowledged -= OnUnacknowledged;
                awaitingKeyframeAfterGap = false;
                kaTimer?.Dispose();
                kaTimer = null;
                stallTimer?.Dispose();
                stallTimer = null;
                try
                {
                    session.StopLiveMedia(opts.Channel, opts.AccountId, opts.HomeBaseAttached);
                }
                catch (Exception e)
                {
                    logger.LogDebug("[live] stopLiveMedia ignored: {Message}", e.Message);
                }
            }
        }

        private void OnVideoGap()
        {
            lock (gate)
            {
                units.Discard();
                awaitingKeyframeAfterGap = true;
            }
        }

        private void OnUnacknowledged(int channel)
        {
            if (channel == opts.Channel) Unacknowledged?.Invoke();
        }

        private void SendStart(bool force = false)
        {
            try
            {
                session.StartLiveMedia(opts.Channel, opts.AccountId, opts.HomeBaseAttached, force);
            }
            catch (Exception e)
            {
                logger.LogDebug("[live] startLiveMedia deferred (session not ready): {Message}", e.Message);
            }
        }

        /// <summary>Once an attached station serves this camera, stop re-sending the start and watch for the channel going silent instead.</summary>
        private void SettleKeepalive()
        {
            if (!opts.HomeBaseAttached) return;
            ArmStallWatch();
            if (kaTimer is null) return;
            kaTimer.Dispose();
            kaTimer = null;
            logger.LogDebug("[live ch{Channel}] station is serving this camera — holding off the attached restart", opts.Channel);
        }

        private void ArmStallWatch()
        {
            stallTimer?.Dispose();
            stallTimer = null;
            if (opts.KeepAlive <= TimeSpan.Zero) return;
            var stall = opts.Stall ?? opts.KeepAlive * 2;
            stallTimer = opts.Time.CreateTimer(_ =>
            {
                lock (gate)
                {
                    if (!listening || kaTimer is not null) return;
                    logger.LogDebug("[live ch{Channel}] no own media for {Stall} — re-asserting this camera's channel", opts.Channel, stall);
                    SendStart();
                    kaTimer = opts.Time.CreateTimer(_ => { lock (gate) if (listening) SendStart(); }, null, opts.KeepAlive, opts.KeepAlive);
                }
            }, null, stall, Timeout.InfiniteTimeSpan);
        }

        private static byte[]? PlaintextAnnexB(byte[] d)
        {
            if (d.Length <= VideoHeaderLength) return null;
            var body = d.AsSpan(VideoHeaderLength);
            if (body.Length >= 4 && (body[..4].SequenceEqual(new byte[] { 0, 0, 0, 1 }) || body[..3].SequenceEqual(new byte[] { 0, 0, 1 })))
                return body.ToArray();
            return null;
        }

        private bool AcceptsMedia(P2PFrame frame) =>
            mediaChannel is null || (frame.CommandId != P2PCommandType.VideoFrame && frame.CommandId != P2PCommandType.AudioFrame) || frame.Channel == mediaChannel;

        private byte[]? AnnexBOf(byte[] payload, int signCode) =>
            session.DecodeVideoFrame(payload, signCode) ?? PlaintextAnnexB(payload) ?? decoder?.DecodeFrame(payload)?.Data;

        private void OnFrame(P2PFrame f)
        {
            if (f.CommandId != P2PCommandType.VideoFrame || !AcceptsMedia(f)) return;
            List<LiveVideoFrame> ready = [];
            lock (gate)
            {
                if (!listening) return;
                foreach (var unit in units.Push(f.Data, payload => AnnexBOf(payload, f.SignCode)))
                {
                    if (awaitingKeyframeAfterGap)
                    {
                        if (!unit.Keyframe) continue;
                        awaitingKeyframeAfterGap = false;
                    }
                    if (unit.Keyframe) lastCodec = AnnexB.SniffCodec(unit.Data) ?? lastCodec;
                    SettleKeepalive();
                    ready.Add(new LiveVideoFrame(unit.SourceTimestamp, unit.Keyframe, unit.Width, unit.Height, lastCodec, unit.Data));
                }
            }
            foreach (var frame in ready) Video?.Invoke(frame);
        }

        private void ReportDroppedUnit(int carried, int chunks, int count)
        {
            awaitingKeyframeAfterGap = true;
            if (count == 1) logger.LogWarning("[live] dropped an incomplete access unit ({Carried} bytes in {Chunks} frame(s), tail never arrived, {Count} so far)", carried, chunks, count);
            else logger.LogDebug("[live] dropped an incomplete access unit ({Carried} bytes in {Chunks} frame(s), tail never arrived, {Count} so far)", carried, chunks, count);
        }
    }
}
