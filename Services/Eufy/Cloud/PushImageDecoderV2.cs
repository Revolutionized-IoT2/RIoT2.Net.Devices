// Ported from eufy-sdk src/transport/http/decodeImageV2.ts @ 5f92bea5 (Apache-2.0), modified for RIoT2.

using System.Text;

namespace RIoT2.Net.Devices.Services.Eufy.Cloud
{
    /// <summary>
    /// The keyless v2 <c>v2_eufysecurity:</c> push-thumbnail decoder. A JPEG prefix is obfuscated and the rest is plain
    /// JPEG: the tail from the chroma Huffman tables (or from a DRI + SOS) on. The decoder recovers subsampling and
    /// geometry from the entropy scan, splices a standard header on and corrects the lost quantization scale.
    /// </summary>
    /// <remarks>
    /// Upstream uses jpeg-js for its one probe decode; here <see cref="BaselineJpegDecoder"/> does that job. Its
    /// rounding differs from jpeg-js by at most a level per sample, which can move the measured stretch slightly.
    /// </remarks>
    internal static class PushImageDecoderV2
    {
        private const string V2Prefix = "v2_eufysecurity:";

        private static readonly byte[] DriSegment = [0xff, 0xdd, 0x00, 0x04];
        private static readonly byte[] SosHeader = [0xff, 0xda, 0x00, 0x0c, 0x03];

        private static readonly int[] ZigZag =
        [
            0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5, 12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
            35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51, 58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47,
            55, 62, 63,
        ];

        private static readonly int[] QuantLuma =
        [
            16, 11, 10, 16, 24, 40, 51, 61, 12, 12, 14, 19, 26, 58, 60, 55, 14, 13, 16, 24, 40, 57, 69, 56,
            14, 17, 22, 29, 51, 87, 80, 62, 18, 22, 37, 56, 68, 109, 103, 77, 24, 35, 55, 64, 81, 104, 113, 92,
            49, 64, 78, 87, 103, 121, 120, 101, 72, 92, 95, 98, 112, 100, 103, 99,
        ];

        private static readonly int[] QuantChroma =
        [
            17, 18, 24, 47, 99, 99, 99, 99, 18, 21, 26, 66, 99, 99, 99, 99, 24, 26, 56, 99, 99, 99, 99, 99,
            47, 66, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99,
            99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99,
        ];

        internal static readonly byte[] DhtDcLuma = Convert.FromHexString("ffc4001f0000010501010101010100000000000000000102030405060708090a0b");

        internal static readonly byte[] DhtAcLuma = Convert.FromHexString(
            "ffc400b5100002010303020403050504040000017d01020300041105122131410613516107227114328191a1082342b1c1" +
            "1552d1f02433627282090a161718191a25262728292a3435363738393a434445464748494a535455565758595a636465666768" +
            "696a737475767778797a838485868788898a92939495969798999aa2a3a4a5a6a7a8a9aab2b3b4b5b6b7b8b9bac2c3c4c5c6c7" +
            "c8c9cad2d3d4d5d6d7d8d9dae1e2e3e4e5e6e7e8e9eaf1f2f3f4f5f6f7f8f9fa");

        private static readonly byte[] DhtDcChroma = Convert.FromHexString("ffc4001f0100030101010101010101010000000000000102030405060708090a0b");

        /// <summary>The standard DC-chrominance Huffman marker: the first plaintext bytes of a v2 tail.</summary>
        internal static readonly byte[] DcChroma = DhtDcChroma[..5];

        private static readonly byte[] DhtAcChroma = Convert.FromHexString(
            "ffc400b51100020102040403040705040400010277000102031104052131061241510761711322328108144291a1b1c1" +
            "09233352f0156272d10a162434e125f11718191a262728292a35363738393a434445464748494a535455565758595a636465" +
            "666768696a737475767778797a82838485868788898a92939495969798999aa2a3a4a5a6a7a8a9aab2b3b4b5b6b7b8b9bac2" +
            "c3c4c5c6c7c8c9cad2d3d4d5d6d7d8d9dae2e3e4e5e6e7e8e9eaf2f3f4f5f6f7f8f9fa");

        private static readonly byte[] Soi = [0xff, 0xd8];
        private static readonly byte[] App0 = Convert.FromHexString("ffe000104a46494600010100000100010000");

        /// <summary>The quality the substitute quant tables start at, before the contrast correction.</summary>
        private const int ReferenceQuality = 85;
        private const double MaxStretch = 8;
        private const double CutoffPercent = 0.5;
        private const int MaxEdge = 4096;
        private const double MinAspect = 0.4;
        private const double MaxAspect = 4;

        private sealed record Geometry(int Subsampling, int Width, int Height, double Shear);

        private static (int W, int H) Mcu(int subsampling) => subsampling switch { 1 => (16, 8), 2 => (16, 16), _ => (8, 8) };

        private static int[] ScaleQuant(int[] baseTable, int quality)
        {
            var factor = quality < 50 ? 5000 / quality : 200 - quality * 2;
            return baseTable.Select(v => Math.Min(255, Math.Max(1, (v * factor + 50) / 100))).ToArray();
        }

        private static byte[] DqtSegment(int[] table, int id, int quality)
        {
            var q = ScaleQuant(table, quality);
            var segment = new byte[69];
            segment[0] = 0xff;
            segment[1] = 0xdb;
            segment[2] = 0x00;
            segment[3] = 0x43;
            segment[4] = (byte)id;
            for (var i = 0; i < 64; i++)
                segment[5 + i] = (byte)q[ZigZag[i]];
            return segment;
        }

        private static byte[] SofSegment(int width, int height, int subsampling)
        {
            byte samplingFactors = subsampling == 2 ? (byte)0x22 : subsampling == 1 ? (byte)0x21 : (byte)0x11;
            return
            [
                0xff, 0xc0, 0x00, 0x11, 0x08, (byte)((height >> 8) & 0xff), (byte)(height & 0xff), (byte)((width >> 8) & 0xff), (byte)(width & 0xff),
                0x03, 0x01, samplingFactors, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01,
            ];
        }

        /// <summary>A standard baseline header for the geometry, ending right before the DC-chroma DHT.</summary>
        private static byte[] BuildHeader(int width, int height, int subsampling, int quality) =>
            [.. Soi, .. App0, .. DqtSegment(QuantLuma, 0, quality), .. DqtSegment(QuantChroma, 1, quality), .. SofSegment(width, height, subsampling), .. DhtDcLuma, .. DhtAcLuma];

        private static DecodedJpeg? DecodeSpliced(byte[] header, byte[] tail) => BaselineJpegDecoder.Decode([.. header, .. tail]);

        /// <summary>One channel's range with <paramref name="cutoff"/>% of the pixels trimmed off each end.</summary>
        private static (int Lo, int Hi) TrimmedRange(byte[] rgb, int channel, int total, double cutoff)
        {
            var hist = new int[256];
            for (var i = 0; i < total; i++)
                hist[rgb[i * 3 + channel]]++;

            var remaining = (long)Math.Floor(total * cutoff / 100);
            var lo = 0;
            while (lo < 255 && remaining > 0)
            {
                if (remaining < hist[lo])
                    break;
                remaining -= hist[lo];
                lo++;
            }
            remaining = (long)Math.Floor(total * cutoff / 100);
            var hi = 255;
            while (hi > 0 && remaining > 0)
            {
                if (remaining < hist[hi])
                    break;
                remaining -= hist[hi];
                hi--;
            }
            return (lo, hi);
        }

        /// <summary>How much to amplify the reconstruction (as a factor on its quant tables) to open it to the full range.</summary>
        private static double ContrastScale(DecodedJpeg img, double cutoff = CutoffPercent)
        {
            var total = img.Width * img.Height;
            if (total == 0)
                return 1;
            var stretch = double.PositiveInfinity;
            for (var channel = 0; channel < 3; channel++)
            {
                var (lo, hi) = TrimmedRange(img.Rgb, channel, total, cutoff);
                if (hi <= lo)
                    continue;
                if (lo < 128)
                    stretch = Math.Min(stretch, 128.0 / (128 - lo));
                if (hi > 128)
                    stretch = Math.Min(stretch, 127.0 / (hi - 128));
            }
            if (double.IsInfinity(stretch))
                return 1;
            return Math.Min(MaxStretch, Math.Max(1, stretch));
        }

        /// <summary>The quality whose Annex-K scaling is <paramref name="stretch"/> times the reference quality's.</summary>
        private static int QualityForStretch(double stretch)
        {
            var referenceFactor = ReferenceQuality < 50 ? 5000 / ReferenceQuality : 200 - ReferenceQuality * 2;
            var factor = referenceFactor * stretch;
            var quality = factor <= 100 ? (200 - factor) / 2 : 5000 / factor;
            return (int)Math.Min(99, Math.Max(1, Math.Floor(quality + 0.5)));
        }

        /// <summary>Mean row-to-row difference of the MCU-resolution luma for a frame <paramref name="mcuWidth"/> MCUs across.</summary>
        private static double McuRowShear(int[] luma, int mcuWidth, int mcuHeight)
        {
            if (mcuHeight < 2)
                return double.PositiveInfinity;
            double sum = 0;
            for (var y = 1; y < mcuHeight; y++)
            {
                for (var x = 0; x < mcuWidth; x++)
                    sum += Math.Abs(luma[y * mcuWidth + x] - luma[(y - 1) * mcuWidth + x]);
            }
            return sum / (mcuWidth * (double)(mcuHeight - 1));
        }

        /// <summary>The divisor pair of the MCU count that looks most continuous, within the plausible shapes.</summary>
        private static Geometry? BestGeometry(int[] luma, int mcus, int subsampling)
        {
            var (mcuPixelWidth, mcuPixelHeight) = Mcu(subsampling);
            Geometry? best = null;
            for (var mcuWidth = 1; mcuWidth <= mcus; mcuWidth++)
            {
                if (mcus % mcuWidth != 0)
                    continue;
                var mcuHeight = mcus / mcuWidth;
                var width = mcuWidth * mcuPixelWidth;
                var height = mcuHeight * mcuPixelHeight;
                if (width > MaxEdge || height > MaxEdge)
                    continue;
                var aspect = width / (double)height;
                if (aspect < MinAspect || aspect > MaxAspect)
                    continue;
                var shear = McuRowShear(luma, mcuWidth, mcuHeight);
                if (best is null || shear < best.Shear)
                    best = new Geometry(subsampling, width, height, shear);
            }
            return best;
        }

        /// <summary>Recovers subsampling and geometry from the plaintext scan; only the right subsampling completes the walk.</summary>
        private static Geometry? FindGeometry(byte[] tail)
        {
            Geometry? best = null;
            foreach (var subsampling in new[] { 0, 1, 2 })
            {
                var scan = JpegScan.ScanEntropy(tail, subsampling, [DhtDcLuma, DhtAcLuma]);
                if (scan is null || !scan.Complete || scan.Mcus < 1)
                    continue;
                var geometry = BestGeometry(scan.Luma, scan.Mcus, subsampling);
                if (geometry is not null && (best is null || geometry.Shear < best.Shear))
                    best = geometry;
            }
            return best;
        }

        /// <summary>Where the DRI segment in front of the last 3-component SOS starts, or -1.</summary>
        private static int FindScanCut(byte[] data)
        {
            var sos = data.AsSpan().LastIndexOf(SosHeader);
            if (sos < 0)
                return -1;
            var dri = sos - DriSegment.Length - 2;
            return dri >= 0 && data.AsSpan(dri, DriSegment.Length).SequenceEqual(DriSegment) ? dri : -1;
        }

        /// <summary>The plaintext tail from the chroma Huffman tables on, or null when the blob has neither plaintext start.</summary>
        private static byte[]? PlaintextTail(byte[] data)
        {
            var cut = data.AsSpan().IndexOf(DcChroma);
            if (cut >= 0)
                return data[cut..];
            var scan = FindScanCut(data);
            if (scan < 0)
                return null;
            return [.. DhtDcChroma, .. DhtAcChroma, .. data.AsSpan(scan)];
        }

        /// <summary>True for a v2 <c>v2_eufysecurity:</c> blob.</summary>
        public static bool IsV2Image(byte[] data) =>
            data.Length >= V2Prefix.Length && Encoding.Latin1.GetString(data, 0, V2Prefix.Length) == V2Prefix;

        /// <summary>Decodes a v2 blob to a plain JPEG, or null when it isn't v2 or no frame shape explains its scan.</summary>
        public static byte[]? DecodeImageV2(byte[] data)
        {
            if (!IsV2Image(data))
                return null;
            var tail = PlaintextTail(data);
            if (tail is null)
                return null;

            var geometry = FindGeometry(tail);
            if (geometry is null)
                return null;

            var reference = BuildHeader(geometry.Width, geometry.Height, geometry.Subsampling, ReferenceQuality);
            var probe = DecodeSpliced(reference, tail);
            if (probe is null)
                return null;

            var stretch = ContrastScale(probe);
            var header = stretch > 1.01 ? BuildHeader(geometry.Width, geometry.Height, geometry.Subsampling, QualityForStretch(stretch)) : reference;
            return [.. header, .. tail];
        }
    }
}
