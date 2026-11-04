// Ported from eufy-sdk src/transport/p2p/video.ts @ 5f92bea5 (Apache-2.0), modified for RIoT2.

using System.Buffers.Binary;
using System.Security.Cryptography;

namespace RIoT2.Net.Devices.Services.Eufy.P2P
{
    /// <summary>The 22-byte <c>CMD_VIDEO_FRAME</c> header.</summary>
    internal readonly record struct VideoFrameHeader(uint PayloadLength, bool Keyframe, int Flags, int Sequence, int Width, int Height, uint Timestamp);

    /// <summary>One complete access unit, its chunks joined.</summary>
    internal sealed record AssembledAccessUnit(uint SourceTimestamp, bool Keyframe, int Width, int Height, byte[] Data);

    internal static class VideoFrames
    {
        public const int HeaderLength = 0x16;

        /// <summary>
        /// The size of a full chunk a station splits a large access unit into: a unit whose header declares this
        /// length continues in the next frame.
        /// </summary>
        public const uint StationChunkBytes = 64000;

        public static VideoFrameHeader? ParseHeader(byte[] payload)
        {
            if (payload.Length < HeaderLength) return null;
            var flags = payload[0x04];
            return new VideoFrameHeader(
                BinaryPrimitives.ReadUInt32LittleEndian(payload),
                (flags & 0x01) == 1,
                flags,
                BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(0x06)),
                BinaryPrimitives.ReadInt16LittleEndian(payload.AsSpan(0x0a)),
                BinaryPrimitives.ReadInt16LittleEndian(payload.AsSpan(0x0c)),
                BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(0x0e)));
        }
    }

    /// <summary>
    /// Decodes the E2E-sealed video frames of cameras whose media key is ECIES-wrapped for the cipher's ECC key: a
    /// keyframe carries the 129-byte envelope at 0x16, every frame a GCM tag at 0x97, a nonce at 0xa7 and the body at 0xb3.
    /// </summary>
    internal sealed class VideoFrameDecoder
    {
        private const int EnvelopeOffset = 0x16;
        private const int EnvelopeLength = 0x81;
        private const int GcmTagOffset = 0x97;
        private const int GcmNonceOffset = 0xa7;
        private const int BodyOffset = 0xb3;
        private static readonly byte[] Aad = "eufy security"u8.ToArray();

        private readonly string eccPrivateKeyHex;
        private byte[]? mediaKey;

        public VideoFrameDecoder(byte[] eccPrivateKey)
        {
            if (eccPrivateKey.Length != 32) throw new ArgumentException("eccPrivateKey must be 32 bytes", nameof(eccPrivateKey));
            eccPrivateKeyHex = Convert.ToHexStringLower(eccPrivateKey);
        }

        public byte[]? CurrentMediaKey => mediaKey?.ToArray();

        public (bool Keyframe, byte[] Data, int Width, int Height)? DecodeFrame(byte[] payload)
        {
            if (VideoFrames.ParseHeader(payload) is not { } header) return null;
            if (header.Keyframe)
            {
                if (payload.Length < EnvelopeOffset + EnvelopeLength) return null;
                var key = P2PCodec.EciesUnwrap(payload.AsSpan(EnvelopeOffset, EnvelopeLength), eccPrivateKeyHex, verifyHmac: true, pkcs7: true);
                if (key is not { Length: 32 }) return null;
                mediaKey = key;
            }
            if (mediaKey is null || payload.Length < BodyOffset) return null;
            try
            {
                using var gcm = new AesGcm(mediaKey, 16);
                var body = payload.AsSpan(BodyOffset);
                var plain = new byte[body.Length];
                gcm.Decrypt(payload.AsSpan(GcmNonceOffset, 12), body, payload.AsSpan(GcmTagOffset, 16), plain, Aad);
                return (header.Keyframe, plain, header.Width, header.Height);
            }
            catch (CryptographicException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Joins the chunks a station splits a large access unit into: a frame whose header declares
    /// <see cref="VideoFrames.StationChunkBytes"/> continues in the next frame with the same timestamp and sequence.
    /// </summary>
    internal sealed class AccessUnitAssembler
    {
        private (VideoFrameHeader Header, List<byte[]> Chunks, int Carried)? open;
        private int droppedUnits;
        private readonly Action<int, int, int>? onDropped;

        public AccessUnitAssembler(Action<int, int, int>? onDropped = null)
        {
            this.onDropped = onDropped;
        }

        public IReadOnlyList<AssembledAccessUnit> Push(byte[] payload, Func<byte[], byte[]?> decode)
        {
            var header = VideoFrames.ParseHeader(payload);
            var body = header is null ? null : decode(payload);
            if (header is not { } h || body is not { Length: > 0 })
            {
                Discard();
                return [];
            }
            var full = h.PayloadLength == VideoFrames.StationChunkBytes;
            if (open is { } o && SameUnit(o.Header, h) && !BeginsAccessUnit(body))
            {
                o.Chunks.Add(body);
                var carried = o.Carried + body.Length;
                if (full)
                {
                    open = (o.Header, o.Chunks, carried);
                    return [];
                }
                open = null;
                return [UnitOf(o.Header, [.. o.Chunks.SelectMany(c => c)])];
            }
            Discard();
            if (!BeginsAccessUnit(body)) return [];
            if (full)
            {
                open = (h, [body], body.Length);
                return [];
            }
            return [UnitOf(h, body)];
        }

        /// <summary>Drops the unit in flight (its tail never arrived).</summary>
        public void Discard()
        {
            if (open is not { } o) return;
            open = null;
            droppedUnits++;
            onDropped?.Invoke(o.Carried, o.Chunks.Count, droppedUnits);
        }

        private static bool SameUnit(VideoFrameHeader open, VideoFrameHeader next) => next.Timestamp == open.Timestamp && next.Sequence == open.Sequence;

        private static bool BeginsAccessUnit(byte[] body) =>
            body.Length >= 4 && body[0] == 0 && body[1] == 0 && (body[2] == 1 || (body[2] == 0 && body[3] == 1));

        private static AssembledAccessUnit UnitOf(VideoFrameHeader header, byte[] data) =>
            new(header.Timestamp, header.Keyframe, header.Width, header.Height, data);
    }
}
