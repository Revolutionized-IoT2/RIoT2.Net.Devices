// Ported from eufy-sdk src/transport/http/jpeg-scan.ts @ 5f92bea5 (Apache-2.0), modified for RIoT2.

namespace RIoT2.Net.Devices.Services.Eufy.Cloud
{
    /// <summary>What a baseline scan carried under one subsampling hypothesis.</summary>
    /// <param name="Mcus">Complete MCUs decoded before the data ran out.</param>
    /// <param name="Luma">Mean luma DC per MCU, in scan order.</param>
    /// <param name="Complete">Whether the scan ended on a whole MCU with nothing but the EOI marker left.</param>
    internal sealed record EntropyScan(int Mcus, int[] Luma, bool Complete);

    /// <summary>
    /// A baseline-JPEG entropy scanner that decodes no pixels: it walks the Huffman-coded coefficients, keeps one
    /// luma DC per MCU and reports whether the scan fills a whole number of MCUs under a subsampling hypothesis.
    /// </summary>
    internal static class JpegScan
    {
        private const byte Dht = 0xc4;
        private const byte Sos = 0xda;
        private const byte Dri = 0xdd;
        private const byte RstFirst = 0xd0;
        private const byte RstLast = 0xd7;

        /// <summary>Huffman table in the spec's decode form (F.2.2.3).</summary>
        internal sealed class HuffmanTable
        {
            public int[] MinCode { get; } = new int[17];
            public int[] MaxCode { get; } = Enumerable.Repeat(-1, 17).ToArray();
            public int[] ValPtr { get; } = new int[17];
            public byte[] Values { get; init; } = [];
        }

        private sealed class ScanComponent(int h, int v, HuffmanTable dc, HuffmanTable ac)
        {
            public int H { get; } = h;
            public int V { get; } = v;
            public HuffmanTable Dc { get; } = dc;
            public HuffmanTable Ac { get; } = ac;
            public int Pred { get; set; }
        }

        private sealed class ScanEndException : Exception
        {
            public ScanEndException(string message) : base(message) { }
        }

        /// <summary>Parses every table of a DHT segment (<c>FF C4</c> included) into <c>class&lt;&lt;4 | id</c> → table.</summary>
        internal static void ReadHuffmanTables(ReadOnlySpan<byte> segment, Dictionary<int, HuffmanTable> into)
        {
            if (segment.Length < 4)
                return;
            var end = Math.Min(segment.Length, 2 + ((segment[2] << 8) | segment[3]));
            var at = 4;
            while (at + 17 <= end)
            {
                int id = segment[at];
                var counts = segment.Slice(at + 1, 16);
                var total = 0;
                foreach (var count in counts)
                    total += count;
                var available = Math.Max(0, Math.Min(total, segment.Length - (at + 17)));
                var table = new HuffmanTable { Values = segment.Slice(at + 17, available).ToArray() };
                var code = 0;
                var index = 0;
                for (var length = 1; length <= 16; length++)
                {
                    int count = counts[length - 1];
                    if (count > 0)
                    {
                        table.ValPtr[length] = index;
                        table.MinCode[length] = code;
                        code += count;
                        index += count;
                        table.MaxCode[length] = code - 1;
                    }
                    code <<= 1;
                }
                into[id] = table;
                at += 17 + total;
            }
        }

        /// <summary>A bit reader over entropy-coded data: unstuffs <c>FF 00</c>, resynchronises on RSTn, ends at any other marker.</summary>
        internal sealed class BitReader(byte[] data, int start)
        {
            private int _bits;
            private int _count;

            public int Position { get; private set; } = start;
            public bool Restarted { get; set; }

            public int ReadBit()
            {
                if (_count == 0)
                {
                    if (Position >= data.Length)
                        throw new ScanEndException("out of data");
                    int b = data[Position++];
                    if (b == 0xff)
                    {
                        int next = Position < data.Length ? data[Position] : -1;
                        if (next == 0x00)
                        {
                            Position++;
                        }
                        else if (next >= RstFirst && next <= RstLast)
                        {
                            Position++;
                            Restarted = true;
                            if (Position >= data.Length)
                                throw new ScanEndException("out of data");
                            b = data[Position++];
                            if (b == 0xff)
                                throw new ScanEndException("marker after restart");
                        }
                        else
                        {
                            Position--;
                            throw new ScanEndException("marker");
                        }
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

            public void Align() => _count = 0;
        }

        private static int DecodeHuffman(BitReader reader, HuffmanTable table)
        {
            var code = reader.ReadBit();
            for (var length = 1; length <= 16; length++)
            {
                if (table.MaxCode[length] >= 0 && code <= table.MaxCode[length])
                {
                    var index = table.ValPtr[length] + code - table.MinCode[length];
                    if (index < 0 || index >= table.Values.Length)
                        throw new ScanEndException("huffman value out of range");
                    return table.Values[index];
                }
                code = (code << 1) | reader.ReadBit();
            }
            throw new ScanEndException("no huffman code of any length");
        }

        /// <summary>Sign-extends a <paramref name="length"/>-bit magnitude (spec F.12).</summary>
        internal static int Extend(int value, int length) => value < 1 << (length - 1) ? value - (1 << length) + 1 : value;

        private static int DecodeBlock(BitReader reader, ScanComponent component)
        {
            var dcLength = DecodeHuffman(reader, component.Dc);
            if (dcLength > 16)
                throw new ScanEndException("dc magnitude out of range");
            component.Pred += dcLength == 0 ? 0 : Extend(reader.ReadBits(dcLength), dcLength);

            var k = 1;
            while (k < 64)
            {
                var rs = DecodeHuffman(reader, component.Ac);
                var size = rs & 15;
                var run = rs >> 4;
                if (size == 0)
                {
                    if (run != 15)
                        break;
                    k += 16;
                }
                else
                {
                    k += run;
                    if (k > 63)
                        throw new ScanEndException("ac coefficient index out of range");
                    reader.ReadBits(size);
                    k++;
                }
            }
            return component.Pred;
        }

        private static int At(byte[] data, int index) => index >= 0 && index < data.Length ? data[index] : 0;

        /// <summary>
        /// Walks the tail's scan under one subsampling hypothesis (0 = 4:4:4, 1 = 4:2:2, 2 = 4:2:0).
        /// <paramref name="extraTables"/> are DHT segments not in the tail. Null when the tail is not a baseline scan.
        /// </summary>
        public static EntropyScan? ScanEntropy(byte[] tail, int subsampling, IReadOnlyList<byte[]> extraTables)
        {
            var tables = new Dictionary<int, HuffmanTable>();
            foreach (var segment in extraTables)
                ReadHuffmanTables(segment, tables);

            var at = 0;
            var restartInterval = 0;
            var scanStart = -1;
            var assignments = new List<(int Dc, int Ac)>();
            while (at + 3 < tail.Length)
            {
                if (tail[at] != 0xff)
                    return null;
                var marker = tail[at + 1];
                var length = (tail[at + 2] << 8) | tail[at + 3];
                if (marker == Dht)
                {
                    var segmentEnd = Math.Min(tail.Length, at + 2 + length);
                    ReadHuffmanTables(tail.AsSpan(at, segmentEnd - at), tables);
                }
                else if (marker == Dri)
                {
                    restartInterval = (At(tail, at + 4) << 8) | At(tail, at + 5);
                }
                else if (marker == Sos)
                {
                    var count = At(tail, at + 4);
                    assignments.Clear();
                    for (var i = 0; i < count; i++)
                    {
                        var tableByte = At(tail, at + 6 + i * 2);
                        assignments.Add((tableByte >> 4, tableByte & 15));
                    }
                    scanStart = at + 2 + length;
                    break;
                }
                at += 2 + length;
            }
            if (scanStart < 0 || assignments.Count != 3)
                return null;

            var (lumaH, lumaV) = subsampling switch { 1 => (2, 1), 2 => (2, 2), _ => (1, 1) };
            var components = new List<ScanComponent>();
            for (var index = 0; index < assignments.Count; index++)
            {
                if (!tables.TryGetValue(assignments[index].Dc, out var dc) || !tables.TryGetValue(0x10 | assignments[index].Ac, out var ac))
                    return null;
                components.Add(new ScanComponent(index == 0 ? lumaH : 1, index == 0 ? lumaV : 1, dc, ac));
            }

            var reader = new BitReader(tail, Math.Min(scanStart, tail.Length));
            var luma = new int[1024];
            var lumaBlocks = lumaH * lumaV;
            var mcus = 0;
            var consumed = reader.Position;
            var complete = false;
            try
            {
                for (; ; )
                {
                    if (restartInterval > 0 && mcus > 0 && mcus % restartInterval == 0)
                    {
                        reader.Align();
                        foreach (var component in components)
                            component.Pred = 0;
                    }
                    if (reader.Restarted)
                    {
                        reader.Restarted = false;
                        foreach (var component in components)
                            component.Pred = 0;
                    }
                    var lumaSum = 0;
                    foreach (var component in components)
                    {
                        for (var block = 0; block < component.H * component.V; block++)
                        {
                            var dc = DecodeBlock(reader, component);
                            if (component == components[0])
                                lumaSum += dc;
                        }
                    }
                    if (mcus == luma.Length)
                        Array.Resize(ref luma, luma.Length * 2);
                    luma[mcus] = (int)Math.Floor(lumaSum / (double)lumaBlocks + 0.5);
                    mcus++;
                    consumed = reader.Position;
                    if (consumed >= tail.Length)
                    {
                        complete = true;
                        break;
                    }
                }
            }
            catch (ScanEndException)
            {
                complete = tail.Length - consumed <= 2;
            }
            return new EntropyScan(mcus, luma[..mcus], complete);
        }
    }
}
