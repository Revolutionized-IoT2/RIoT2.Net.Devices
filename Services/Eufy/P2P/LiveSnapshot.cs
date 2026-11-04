// Ported from eufy-sdk src/transport/p2p/media.ts (captureSnapshotFromShared, primeForDecode) @ 5f92bea5
// (Apache-2.0), modified for RIoT2. The snapshot drives its own LiveStream instead of a shared live source.

using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace RIoT2.Net.Devices.Services.Eufy.P2P
{
    internal sealed class LiveSnapshotOptions
    {
        /// <summary>
        /// Keyframes skipped before one is captured: a cold stream's first IDR is frequently partial. The skipped one is
        /// kept as a fallback for when no later keyframe arrives within <see cref="FallbackAfter"/>.
        /// </summary>
        public int SkipKeyframes { get; init; } = 1;
        public TimeSpan FallbackAfter { get; init; } = TimeSpan.FromSeconds(12);
        public required string FfmpegExecutable { get; init; }
        /// <summary>Decodes a primed Annex-B burst to JPEG; defaults to <see cref="Ffmpeg.AnnexBToJpegAsync"/>.</summary>
        public Func<byte[], CancellationToken, Task<byte[]>>? Decode { get; init; }
        public TimeProvider Time { get; init; } = TimeProvider.System;
    }

    /// <summary>A still from a camera's live stream.</summary>
    internal static class LiveSnapshot
    {
        /// <summary>
        /// Makes a burst decodable on its own by re-emitting the stream's parameter sets ahead of it; sets from another
        /// codec are not used.
        /// </summary>
        public static byte[] PrimeForDecode(byte[] burst, ParamSets? sets, VideoCodec codec) =>
            sets is not null && sets.Codec == codec ? AnnexB.PrefixParamSets(burst, sets) : burst;

        /// <summary>
        /// Starts the camera's live stream, collects video until a decodable keyframe (parameter sets known plus an
        /// IDR/IRAP picture), decodes it to JPEG and stops the stream. An undecodable burst is followed by the next keyframe.
        /// </summary>
        public static async Task<byte[]> CaptureAsync(P2PSession session, LiveStreamOptions streamOptions, LiveSnapshotOptions options, ILogger logger, CancellationToken cancellationToken)
        {
            var candidates = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
            var gate = new object();
            ParamSets? sets = null;
            var keyCount = 0;
            byte[]? fallback = null;
            var fallbackDue = false;
            var stream = new LiveStream(session, streamOptions, logger);

            void OnVideo(LiveVideoFrame frame)
            {
                lock (gate)
                {
                    sets = AnnexB.UpdatedParamSets(frame.Data, sets);
                    if (!frame.Keyframe) return;
                    keyCount++;
                    if (!AnnexB.IsDecodableKeyFrame(frame.Data, sets)) return;
                    var primed = PrimeForDecode(frame.Data, sets, frame.Codec);
                    if (keyCount <= options.SkipKeyframes && !fallbackDue)
                    {
                        fallback ??= primed;
                        return;
                    }
                    candidates.Writer.TryWrite(primed);
                }
            }

            void OnClosed() => candidates.Writer.TryComplete(new EufyStationUnreachableException(session.StationSn, $"the P2P session to station {session.StationSn} closed before a keyframe arrived"));

            using var fallbackTimer = options.Time.CreateTimer(_ =>
            {
                lock (gate)
                {
                    fallbackDue = true;
                    if (fallback is not null) candidates.Writer.TryWrite(fallback);
                }
            }, null, options.FallbackAfter, Timeout.InfiniteTimeSpan);

            stream.Video += OnVideo;
            session.Closed += OnClosed;
            var decode = options.Decode ?? ((burst, ct) => Ffmpeg.AnnexBToJpegAsync(burst, options.FfmpegExecutable, logger, ct));
            try
            {
                stream.Start();
                var attempts = 0;
                await foreach (var burst in candidates.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    attempts++;
                    try
                    {
                        return await decode(burst, cancellationToken).ConfigureAwait(false);
                    }
                    catch (SnapshotDecodeException e)
                    {
                        logger.LogDebug("[snapshot] keyframe {Attempt} did not decode: {Message}", attempts, e.Message);
                    }
                }
                throw new EufyP2PException($"the live stream of station {session.StationSn} ended before a keyframe decoded");
            }
            finally
            {
                session.Closed -= OnClosed;
                stream.Video -= OnVideo;
                stream.Stop();
            }
        }
    }
}
