// Ported from eufy-sdk src/transport/http/decodeImageV2.ts (its jpeg-js probe decode) @ 5f92bea5 (Apache-2.0), modified for RIoT2:
// a minimal baseline JPEG decoder written for RIoT2 replaces the jpeg-js dependency.

namespace RIoT2.Net.Devices.Services.Eufy.Cloud
{
    /// <summary>A decoded picture: <see cref="Rgb"/> holds 3 bytes per pixel, row by row.</summary>
    internal sealed record DecodedJpeg(int Width, int Height, byte[] Rgb);

    /// <summary>
    /// Decodes a baseline sequential (SOF0/SOF1, Huffman) JPEG with one interleaved scan, or a one-component
    /// one, to RGB. Anything else (progressive, arithmetic, truncated, larger than the memory bound) answers null.
    /// </summary>
    internal static class BaselineJpegDecoder
    {
        /// <summary>The most the decode may allocate for its planes and output (eufy-sdk's 24 MB probe bound).</summary>
        public const long MaxMemoryBytes = 24L * 1024 * 1024;

        private static readonly int[] ZigZag =
        [
            0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5, 12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
            35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51, 58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47,
            55, 62, 63,
        ];

        private static readonly double[,] Cos = BuildCos();

        private sealed class Component
        {
            public int Id;
            public int H;
            public int V;
            public int Tq;
            public int Td;
            public int Ta;
            public int Pred;
            public int BlocksPerLine;
            public int BlocksPerColumn;
            public byte[] Plane = [];
            public int Stride;
        }

        private sealed class DecodeException : Exception
        {
            public DecodeException(string message) : base(message) { }
        }

        public static DecodedJpeg? Decode(byte[] data)
        {
            try
            {
                return DecodeCore(data);
            }
            catch (Exception e) when (e is DecodeException or IndexOutOfRangeException or ArgumentException or OverflowException)
            {
                return null;
            }
        }

        private static DecodedJpeg? DecodeCore(byte[] data)
        {
            if (data.Length < 4 || data[0] != 0xff || data[1] != 0xd8)
                return null;
            var quant = new int[4][];
            var tables = new Dictionary<int, JpegScan.HuffmanTable>();
            var components = new List<Component>();
            int width = 0, height = 0, restartInterval = 0, hMax = 1, vMax = 1;
            var frameSeen = false;
            var at = 2;
            while (at + 4 <= data.Length)
            {
                if (data[at] != 0xff)
                    throw new DecodeException("marker expected");
                var marker = data[at + 1];
                if (marker == 0xff)
                {
                    at++;
                    continue;
                }
                if (marker == 0xd8 || (marker >= 0xd0 && marker <= 0xd7) || marker == 0x01)
                {
                    at += 2;
                    continue;
                }
                if (marker == 0xd9)
                    break;
                var length = (data[at + 2] << 8) | data[at + 3];
                var segmentStart = at + 4;
                var segmentEnd = at + 2 + length;
                if (length < 2 || segmentEnd > data.Length)
                    throw new DecodeException("truncated segment");
                switch (marker)
                {
                    case 0xdb:
                        for (var p = segmentStart; p < segmentEnd;)
                        {
                            var pq = data[p] >> 4;
                            var tq = data[p] & 15;
                            p++;
                            var table = new int[64];
                            for (var i = 0; i < 64; i++)
                            {
                                table[ZigZag[i]] = pq == 0 ? data[p] : (data[p] << 8) | data[p + 1];
                                p += pq == 0 ? 1 : 2;
                            }
                            quant[tq & 3] = table;
                        }
                        break;
                    case 0xc4:
                        JpegScan.ReadHuffmanTables(data.AsSpan(at, 2 + length), tables);
                        break;
                    case 0xdd:
                        restartInterval = (data[segmentStart] << 8) | data[segmentStart + 1];
                        break;
                    case 0xc0:
                    case 0xc1:
                        if (data[segmentStart] != 8)
                            throw new DecodeException("only 8-bit precision");
                        height = (data[segmentStart + 1] << 8) | data[segmentStart + 2];
                        width = (data[segmentStart + 3] << 8) | data[segmentStart + 4];
                        var count = data[segmentStart + 5];
                        if (width == 0 || height == 0 || (count != 1 && count != 3))
                            throw new DecodeException("unsupported frame");
                        for (var i = 0; i < count; i++)
                        {
                            var b = segmentStart + 6 + i * 3;
                            var c = new Component { Id = data[b], H = data[b + 1] >> 4, V = data[b + 1] & 15, Tq = data[b + 2] & 3 };
                            if (c.H is < 1 or > 4 || c.V is < 1 or > 4)
                                throw new DecodeException("bad sampling factors");
                            components.Add(c);
                        }
                        hMax = components.Max(c => c.H);
                        vMax = components.Max(c => c.V);
                        if ((long)width * height * 3 + Planes(components, width, height, hMax, vMax) > MaxMemoryBytes)
                            throw new DecodeException("frame exceeds the memory bound");
                        frameSeen = true;
                        break;
                    case 0xc2:
                    case 0xc3:
                    case >= 0xc5 and <= 0xcf when marker != 0xc8 && marker != 0xcc:
                        throw new DecodeException("not a baseline frame");
                    case 0xda:
                        if (!frameSeen)
                            throw new DecodeException("scan before frame");
                        var scanCount = data[segmentStart];
                        var scanComponents = new List<Component>();
                        for (var i = 0; i < scanCount; i++)
                        {
                            var id = data[segmentStart + 1 + i * 2];
                            var c = components.FirstOrDefault(x => x.Id == id) ?? throw new DecodeException("unknown scan component");
                            c.Td = data[segmentStart + 2 + i * 2] >> 4;
                            c.Ta = data[segmentStart + 2 + i * 2] & 15;
                            scanComponents.Add(c);
                        }
                        if (scanComponents.Count != components.Count)
                            throw new DecodeException("only one interleaved scan is supported");
                        DecodeScan(data, segmentEnd, components, tables, quant, width, height, hMax, vMax, restartInterval);
                        return Output(components, width, height, hMax, vMax);
                }
                at = segmentEnd;
            }
            return null;
        }

        private static long Planes(List<Component> components, int width, int height, int hMax, int vMax)
        {
            long total = 0;
            var mcusX = (width + 8 * hMax - 1) / (8 * hMax);
            var mcusY = (height + 8 * vMax - 1) / (8 * vMax);
            foreach (var c in components)
                total += (long)mcusX * c.H * 8 * mcusY * c.V * 8;
            return total;
        }

        private static void DecodeScan(byte[] data, int start, List<Component> components, Dictionary<int, JpegScan.HuffmanTable> tables,
            int[][] quant, int width, int height, int hMax, int vMax, int restartInterval)
        {
            var mcusX = (width + 8 * hMax - 1) / (8 * hMax);
            var mcusY = (height + 8 * vMax - 1) / (8 * vMax);
            foreach (var c in components)
            {
                if (quant[c.Tq] is null)
                    throw new DecodeException("missing quantization table");
                c.BlocksPerLine = mcusX * c.H;
                c.BlocksPerColumn = mcusY * c.V;
                c.Stride = c.BlocksPerLine * 8;
                c.Plane = new byte[c.Stride * c.BlocksPerColumn * 8];
                c.Pred = 0;
            }
            var single = components.Count == 1;
            var only = components[0];
            // A one-component scan is not interleaved: it walks the component's own blocks, not padded MCUs.
            var totalUnits = single
                ? ((width * only.H / hMax + 7) / 8) * ((height * only.V / vMax + 7) / 8)
                : mcusX * mcusY;
            var unitsPerLine = single ? (width * only.H / hMax + 7) / 8 : mcusX;

            var reader = new StrictBitReader(data, start);
            var coefficients = new int[64];
            for (var unit = 0; unit < totalUnits; unit++)
            {
                if (restartInterval > 0 && unit > 0 && unit % restartInterval == 0)
                {
                    reader.Restart();
                    foreach (var c in components)
                        c.Pred = 0;
                }
                if (single)
                {
                    DecodeBlock(reader, only, tables, quant[only.Tq], coefficients, unit / unitsPerLine, unit % unitsPerLine);
                    continue;
                }
                var mcuRow = unit / mcusX;
                var mcuCol = unit % mcusX;
                foreach (var c in components)
                {
                    for (var v = 0; v < c.V; v++)
                    {
                        for (var h = 0; h < c.H; h++)
                            DecodeBlock(reader, c, tables, quant[c.Tq], coefficients, mcuRow * c.V + v, mcuCol * c.H + h);
                    }
                }
            }
        }

        private static void DecodeBlock(StrictBitReader reader, Component c, Dictionary<int, JpegScan.HuffmanTable> tables, int[] q,
            int[] coefficients, int blockRow, int blockCol)
        {
            if (!tables.TryGetValue(c.Td, out var dcTable) || !tables.TryGetValue(0x10 | c.Ta, out var acTable))
                throw new DecodeException("missing huffman table");
            Array.Clear(coefficients);
            var t = Huffman(reader, dcTable);
            c.Pred += t == 0 ? 0 : JpegScan.Extend(reader.ReadBits(t), t);
            coefficients[0] = c.Pred * q[0];
            var k = 1;
            while (k < 64)
            {
                var rs = Huffman(reader, acTable);
                var s = rs & 15;
                var r = rs >> 4;
                if (s == 0)
                {
                    if (r != 15)
                        break;
                    k += 16;
                    continue;
                }
                k += r;
                if (k > 63)
                    throw new DecodeException("coefficient index out of range");
                var z = ZigZag[k];
                coefficients[z] = JpegScan.Extend(reader.ReadBits(s), s) * q[z];
                k++;
            }
            InverseDct(coefficients, c.Plane, c.Stride, blockRow * 8, blockCol * 8);
        }

        private static int Huffman(StrictBitReader reader, JpegScan.HuffmanTable table)
        {
            var code = reader.ReadBit();
            for (var length = 1; length <= 16; length++)
            {
                if (table.MaxCode[length] >= 0 && code <= table.MaxCode[length])
                    return table.Values[table.ValPtr[length] + code - table.MinCode[length]];
                code = (code << 1) | reader.ReadBit();
            }
            throw new DecodeException("bad huffman code");
        }

        private static double[,] BuildCos()
        {
            var cos = new double[8, 8];
            for (var x = 0; x < 8; x++)
            {
                for (var u = 0; u < 8; u++)
                    cos[x, u] = (u == 0 ? Math.Sqrt(0.5) : 1.0) * Math.Cos((2 * x + 1) * u * Math.PI / 16);
            }
            return cos;
        }

        private static void InverseDct(int[] coefficients, byte[] plane, int stride, int top, int left)
        {
            Span<double> rows = stackalloc double[64];
            for (var v = 0; v < 8; v++)
            {
                for (var x = 0; x < 8; x++)
                {
                    double sum = 0;
                    for (var u = 0; u < 8; u++)
                        sum += Cos[x, u] * coefficients[v * 8 + u];
                    rows[v * 8 + x] = sum / 2;
                }
            }
            for (var x = 0; x < 8; x++)
            {
                for (var y = 0; y < 8; y++)
                {
                    double sum = 0;
                    for (var v = 0; v < 8; v++)
                        sum += Cos[y, v] * rows[v * 8 + x];
                    var value = (int)Math.Round(sum / 2 + 128);
                    plane[(top + y) * stride + left + x] = (byte)Math.Clamp(value, 0, 255);
                }
            }
        }

        private static DecodedJpeg Output(List<Component> components, int width, int height, int hMax, int vMax)
        {
            var rgb = new byte[width * height * 3];
            if (components.Count == 1)
            {
                var c = components[0];
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        var g = c.Plane[(y * c.V / vMax) * c.Stride + x * c.H / hMax];
                        var o = (y * width + x) * 3;
                        rgb[o] = rgb[o + 1] = rgb[o + 2] = g;
                    }
                }
                return new DecodedJpeg(width, height, rgb);
            }
            var (cy, cb, cr) = (components[0], components[1], components[2]);
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    double yy = cy.Plane[(y * cy.V / vMax) * cy.Stride + x * cy.H / hMax];
                    double b = cb.Plane[(y * cb.V / vMax) * cb.Stride + x * cb.H / hMax] - 128;
                    double r = cr.Plane[(y * cr.V / vMax) * cr.Stride + x * cr.H / hMax] - 128;
                    var o = (y * width + x) * 3;
                    rgb[o] = Clamp(yy + 1.402 * r);
                    rgb[o + 1] = Clamp(yy - 0.3441363 * b - 0.71413636 * r);
                    rgb[o + 2] = Clamp(yy + 1.772 * b);
                }
            }
            return new DecodedJpeg(width, height, rgb);
        }

        private static byte Clamp(double value) => value <= 0 ? (byte)0 : value >= 255 ? (byte)255 : (byte)value;

        /// <summary>A bit reader that refuses to run past the scan: a frame the data does not fill is not decoded.</summary>
        private sealed class StrictBitReader(byte[] data, int start)
        {
            private int _position = start;
            private int _bits;
            private int _count;

            public int ReadBit()
            {
                if (_count == 0)
                {
                    if (_position >= data.Length)
                        throw new DecodeException("out of data");
                    int b = data[_position++];
                    if (b == 0xff)
                    {
                        var next = _position < data.Length ? data[_position] : -1;
                        if (next != 0x00)
                            throw new DecodeException("unexpected marker");
                        _position++;
                    }
                    _bits = b;
                    _count = 8;
                }
                _count--;
                return (_bits >> _count) & 1;
            }

            public int ReadBits(int length)
            {
                var value = 0;
                for (var i = 0; i < length; i++)
                    value = (value << 1) | ReadBit();
                return value;
            }

            /// <summary>Drops the partial byte and steps over the RSTn marker that ends a restart interval.</summary>
            public void Restart()
            {
                _count = 0;
                while (_position + 1 < data.Length && !(data[_position] == 0xff && data[_position + 1] >= 0xd0 && data[_position + 1] <= 0xd7))
                {
                    if (data[_position] == 0xff && data[_position + 1] != 0x00 && data[_position + 1] != 0xff)
                        throw new DecodeException("marker where a restart was expected");
                    _position++;
                }
                if (_position + 1 >= data.Length)
                    throw new DecodeException("out of data");
                _position += 2;
            }
        }
    }
}
