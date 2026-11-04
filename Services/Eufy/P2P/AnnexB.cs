// Ported from eufy-sdk src/transport/p2p/annexb.ts @ 5f92bea5 (Apache-2.0), modified for RIoT2.
// The coded-geometry reader is not ported: the snapshot reads its size back from the encoded JPEG.

namespace RIoT2.Net.Devices.Services.Eufy.P2P
{
    internal enum VideoCodec
    {
        H264,
        H265
    }

    /// <summary>The parameter sets a stream announced: SPS/PPS, plus VPS for H.265 (NAL bodies, start codes stripped).</summary>
    internal sealed record ParamSets(VideoCodec Codec, IReadOnlyList<byte[]> Sps, IReadOnlyList<byte[]> Pps, IReadOnlyList<byte[]> Vps);

    /// <summary>Annex-B (start-code delimited) H.264 / H.265 helpers.</summary>
    internal static class AnnexB
    {
        private static readonly byte[] StartCode = [0x00, 0x00, 0x00, 0x01];

        /// <summary>The offsets of each NAL body (just past its 3- or 4-byte start code).</summary>
        internal static IEnumerable<int> NalStarts(byte[] buf)
        {
            for (var i = 0; i + 3 < buf.Length; i++)
            {
                if (buf[i] == 0 && buf[i + 1] == 0 && buf[i + 2] == 1) yield return i + 3;
                else if (i + 4 < buf.Length && buf[i] == 0 && buf[i + 1] == 0 && buf[i + 2] == 0 && buf[i + 3] == 1) yield return i + 4;
            }
        }

        private static int StartCodeBegin(byte[] buf, int payloadOffset)
        {
            var threeByte = payloadOffset - 3;
            if (threeByte >= 1 && buf[threeByte - 1] == 0) return threeByte - 1;
            return threeByte;
        }

        /// <summary>Splits an Annex-B buffer into its NAL bodies.</summary>
        public static IReadOnlyList<byte[]> SplitNals(byte[] buf)
        {
            var offsets = NalStarts(buf).ToList();
            var nals = new List<byte[]>();
            for (var k = 0; k < offsets.Count; k++)
            {
                var end = k + 1 < offsets.Count ? StartCodeBegin(buf, offsets[k + 1]) : buf.Length;
                if (end > offsets[k]) nals.Add(buf[offsets[k]..end]);
            }
            return nals;
        }

        /// <summary>The codec named by a parameter set within the first 64 bytes, or null.</summary>
        public static VideoCodec? SniffCodec(byte[] buf)
        {
            foreach (var p in NalStarts(buf))
            {
                if (p > 64) break;
                var b = buf[p];
                var hevc = (b >> 1) & 0x3f;
                if (hevc is 32 or 33 or 34) return VideoCodec.H265;
                var h264 = b & 0x1f;
                if (h264 is 7 or 8) return VideoCodec.H264;
            }
            return null;
        }

        /// <summary>The parameter sets an access unit carries, or null when it carries none.</summary>
        public static ParamSets? ExtractParamSets(byte[] buf)
        {
            var codec = SniffCodec(buf);
            if (codec is null) return null;
            var sps = new List<byte[]>();
            var pps = new List<byte[]>();
            var vps = new List<byte[]>();
            foreach (var nal in SplitNals(buf))
            {
                var b = nal[0];
                if (codec == VideoCodec.H265)
                {
                    var t = (b >> 1) & 0x3f;
                    if (t == 32) vps.Add(nal);
                    else if (t == 33) sps.Add(nal);
                    else if (t == 34) pps.Add(nal);
                }
                else
                {
                    var t = b & 0x1f;
                    if (t == 7) sps.Add(nal);
                    else if (t == 8) pps.Add(nal);
                }
            }
            if (sps.Count == 0 && pps.Count == 0) return null;
            return new ParamSets(codec.Value, sps, pps, vps);
        }

        /// <summary>
        /// Folds the parameter sets a unit announces into those already known: a kind re-announced replaces the old
        /// one, a kind not re-announced stays in force, and a codec change replaces everything.
        /// </summary>
        public static ParamSets? UpdatedParamSets(byte[] buf, ParamSets? current)
        {
            var announced = ExtractParamSets(buf);
            if (announced is null) return current;
            if (current is null || current.Codec != announced.Codec) return announced;
            return new ParamSets(
                announced.Codec,
                announced.Sps.Count > 0 ? announced.Sps : current.Sps,
                announced.Pps.Count > 0 ? announced.Pps : current.Pps,
                announced.Vps.Count > 0 ? announced.Vps : current.Vps);
        }

        /// <summary>Re-emits the known parameter sets (VPS, SPS, PPS) ahead of a unit so it decodes on its own.</summary>
        public static byte[] PrefixParamSets(byte[] annexb, ParamSets sets)
        {
            var ordered = sets.Vps.Concat(sets.Sps).Concat(sets.Pps).ToList();
            if (ordered.Count == 0) return annexb;
            var output = new List<byte>(annexb.Length + ordered.Sum(n => n.Length + 4));
            foreach (var nal in ordered)
            {
                output.AddRange(StartCode);
                output.AddRange(nal);
            }
            output.AddRange(annexb);
            return [.. output];
        }

        /// <summary>True when the unit holds an IDR slice (H.264 type 5; H.265 IDR_W_RADL 19 / IDR_N_LP 20).</summary>
        public static bool HasIdr(byte[] buf, VideoCodec codec)
        {
            foreach (var p in NalStarts(buf))
            {
                var b = buf[p];
                if (codec == VideoCodec.H265)
                {
                    var t = (b >> 1) & 0x3f;
                    if (t is 19 or 20) return true;
                }
                else if ((b & 0x1f) == 5) return true;
            }
            return false;
        }

        /// <summary>True when the unit holds a random-access picture: an H.264 IDR, or any H.265 IRAP (types 16–21).</summary>
        public static bool HasRandomAccessPicture(byte[] buf, VideoCodec codec)
        {
            if (codec == VideoCodec.H264) return HasIdr(buf, codec);
            foreach (var p in NalStarts(buf))
            {
                var t = (buf[p] >> 1) & 0x3f;
                if (t is >= 16 and <= 21) return true;
            }
            return false;
        }

        /// <summary>
        /// True when a decoder can start at this unit given the parameter sets known so far: SPS and PPS (and a VPS for
        /// H.265) are known for the unit's codec, and the unit holds an IDR (H.264) / IRAP (H.265) picture.
        /// </summary>
        public static bool IsDecodableKeyFrame(byte[] unit, ParamSets? sets)
        {
            if (sets is null || sets.Sps.Count == 0 || sets.Pps.Count == 0) return false;
            if (sets.Codec == VideoCodec.H265 && sets.Vps.Count == 0) return false;
            var unitCodec = SniffCodec(unit) ?? sets.Codec;
            return unitCodec == sets.Codec && HasRandomAccessPicture(unit, sets.Codec);
        }
    }
}
