using System.Buffers.Binary;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RIoT2.Net.Devices.Services.Eufy.P2P;

namespace RIoT2.Net.Devices.Tests.Eufy.P2P;

/// <summary>The PPCS wire primitives (port of eufy-sdk <c>p2p.spec.ts</c>, <c>p2p_command.spec.ts</c>, <c>relay.spec.ts</c> vectors).</summary>
[TestClass]
public class P2PCodecTests
{
    private const string Did = "EUPRCAM-000000-XXXXX";

    /// <summary>The inverse of <see cref="P2PCodec.DecodeP2PCloudIPs"/>, so the decoder round-trips synthetic data.</summary>
    private static string EncodeCloudIps(string plain)
    {
        var table = Convert.FromHexString("4959433db5bf6da347534f6165e371e9677f02030badb3892b2f35c16b8b959711e5a70deff1050783fb9d3bc5c713171d1f2529d3df");
        var output = Encoding.UTF8.GetBytes(plain);
        var s = new StringBuilder();
        for (var i = 0; i < output.Length; i++)
        {
            var z = 0x39;
            for (var j = 0; j < i; j++) z ^= output[j];
            var b = (output[i] ^ z ^ table[i % table.Length]) & 0xff;
            s.Append((char)(65 + (b >> 4))).Append((char)(65 + (b & 0x0f)));
        }
        return s.ToString();
    }

    [TestMethod]
    public void DecodesTheCloudLookupServers()
    {
        var conn = EncodeCloudIps("18.197.1.2,3.69.4.5");
        CollectionAssert.AreEqual(new[] { new P2PAddress("18.197.1.2", 32100), new P2PAddress("3.69.4.5", 32100) }, P2PCodec.DecodeP2PCloudIPs(conn).ToArray());
        CollectionAssert.AreEqual(new[] { new P2PAddress("18.197.1.2", 32100) }, P2PCodec.DecodeP2PCloudIPs(EncodeCloudIps("18.197.1.2") + ":suffix").ToArray());
    }

    [TestMethod]
    public void DerivesTheLevel1KeyFromSerialAndDid()
    {
        var key = P2PCodec.P2pCommandEncryptionKey("T8030T0000000000", Did);
        Assert.AreEqual("0000000-000000-X", key);
        Assert.AreEqual(16, key.Length);
    }

    [TestMethod]
    public void EncodesTheDidAsPrefixNumberSuffix()
    {
        var b = P2PCodec.P2pDidToBuffer(Did);
        Assert.HasCount(20, b);
        Assert.AreEqual("EUPRCAM", Encoding.ASCII.GetString(b, 0, 7));
        Assert.AreEqual(0u, BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(8)));
        Assert.AreEqual("XXXXX", Encoding.ASCII.GetString(b, 12, 5));
        Assert.AreEqual(1234u, BinaryPrimitives.ReadUInt32BigEndian(P2PCodec.P2pDidToBuffer("ABCDEFG-001234-ZZZZZ").AsSpan(8)));
    }

    [TestMethod]
    public void FramesAMessageAsTypeLengthPayload()
    {
        Assert.AreEqual("F1300002AABB", Convert.ToHexString(P2PCodec.FrameMessage(P2PMessageType.LocalLookup, [0xaa, 0xbb])));
        Assert.AreEqual("F1E00000", Convert.ToHexString(P2PCodec.FrameMessage(P2PMessageType.Ping)));
        Assert.IsTrue(P2PCodec.HasHeader([0xf1, 0x42, 0, 0], P2PMessageType.CamId));
        Assert.IsFalse(P2PCodec.HasHeader([0xf1], P2PMessageType.CamId));
    }

    [TestMethod]
    public void LaysOutTheClassicLookupWithKey()
    {
        const string dsk = "XXXXXXXXXXXXXXXXXXXX";
        var payload = P2PCodec.BuildLookupWithKeyPayload(Did, "192.0.2.1", 12345, dsk);
        Assert.HasCount(20 + 16 + 4 + dsk.Length + 4, payload);
        CollectionAssert.AreEqual(P2PCodec.P2pDidToBuffer(Did), payload[..20]);
        var self = payload[20..36];
        Assert.AreEqual(0x0002, BinaryPrimitives.ReadUInt16BigEndian(self));
        Assert.AreEqual(12345, BinaryPrimitives.ReadUInt16LittleEndian(self.AsSpan(2)));
        Assert.AreEqual("010200C0", Convert.ToHexString(self, 4, 4));
        CollectionAssert.AreEqual(new byte[8], self[8..]);
        CollectionAssert.AreEqual(new byte[] { 0x02, 0x05, 0x01, 0x05 }, payload[36..40]);
        Assert.AreEqual(dsk, Encoding.ASCII.GetString(payload, 40, dsk.Length));
        CollectionAssert.AreEqual(new byte[4], payload[^4..]);

        // The self address reads back the way a LOOKUP_ADDR answer is read.
        Assert.AreEqual(new P2PAddress("192.0.2.1", 12345), P2PCodec.ParseLookupAddr([0, 0, 0, 0, .. self]));
    }

    [TestMethod]
    public void TheVariant2LookupDiffersOnlyByTheSelfAddressBlock()
    {
        var p1 = P2PCodec.BuildLookupWithKeyPayload(Did, "192.0.2.1", 12345, "KEY");
        var p2 = P2PCodec.BuildLookupWithKeyPayload2(Did, "KEY");
        CollectionAssert.AreEqual(p1[..20], p2[..20]);
        CollectionAssert.AreEqual(p1[40..], p2[20..]);
        Assert.HasCount(p2.Length + 20, p1);
    }

    [TestMethod]
    public void BuildsTheHandshakeAndRelayPayloads()
    {
        var did = P2PCodec.P2pDidToBuffer(Did);
        CollectionAssert.AreEqual(new byte[] { 0, 0 }, P2PCodec.BuildLocalLookupPayload());
        CollectionAssert.AreEqual((byte[])[.. did, 0, 0, 0], P2PCodec.BuildCheckCamPayload(Did));
        CollectionAssert.AreEqual((byte[])[0x01, 0x02, 0x03, 0x04, .. did, 0, 0, 0, 0], P2PCodec.BuildRelayPacketPayload(0x01020304, Did));
        var request = P2PCodec.BuildRelayRequestPayload(Did, new P2PAddress("203.0.113.9", 0x1234), 0xdeadbeef);
        Assert.AreEqual("000234120971 00CB".Replace(" ", "", StringComparison.Ordinal), Convert.ToHexString(request, 20, 8));
        Assert.AreEqual("DEADBEEF", Convert.ToHexString(request, 36, 4));
    }

    [TestMethod]
    public void ParsesTheRelayAnswers()
    {
        byte[] Record(string host, int port) => [.. P2PCodec.BuildRelayRequestPayload(Did, new P2PAddress(host, port), 0)[20..36]];
        byte[] list = [0xf1, 0x69, 0, 0, 3, 0, 0, 0, .. Record("198.51.100.1", 443), .. Record("0.0.0.0", 0), .. Record("198.51.100.2", 10000)];
        CollectionAssert.AreEqual(new[] { new P2PAddress("198.51.100.1", 443), new P2PAddress("198.51.100.2", 10000) }, P2PCodec.ParseRelayList(list).ToArray());
        Assert.HasCount(1, P2PCodec.ParseRelayList(list.AsSpan(0, 8 + 16 + 8)));

        Assert.AreEqual((0x01020304u, 0x5566), P2PCodec.ParseRelayPortAck([0xf1, 0x73, 0, 8, 1, 2, 3, 4, 0x55, 0x66, 0, 0]));
        Assert.IsNull(P2PCodec.ParseRelayPortAck([0xf1, 0x73, 0, 0]));
        var to = P2PCodec.ParseRelayTo([0xf1, 0x82, 0, 20, .. Record("198.51.100.3", 7000), 0, 0, 0, 9]);
        Assert.AreEqual((new P2PAddress("198.51.100.3", 7000), 9u), to);
        Assert.IsNull(P2PCodec.ParseRelayTo([0xf1, 0x82, 0, 0]));
    }

    [TestMethod]
    public void BuildsTheCommandHeaderAndVoidBody()
    {
        Assert.AreEqual("D1001234585A5948" + "4C04", Convert.ToHexString(P2PCodec.BuildCommandHeader(0x1234, 1100)));
        Assert.AreEqual("D1010000" + "585A5948" + "1405", Convert.ToHexString(P2PCodec.BuildCommandHeader(0, 1300, P2PDataType.Video)));
        Assert.AreEqual("00000000" + "0100FF00" + "0000", Convert.ToHexString(P2PCodec.BuildVoidCommandPayload()));
        Assert.AreEqual("0000000001000300" + "0000", Convert.ToHexString(P2PCodec.BuildVoidCommandPayload(3)));
    }

    [TestMethod]
    public void FramesAStringCommandEncryptedUnderTheLevel1Key()
    {
        var key = Encoding.ASCII.GetBytes("0123456789abcdef");
        const string value = "{\"cmd\":1308,\"payload\":[{\"file\":\"/userdata/push/x.jpg\"}]}";
        var buf = P2PCodec.BuildStringCommandPayload(value, 0, key, 1);
        var declared = BinaryPrimitives.ReadUInt16LittleEndian(buf);
        Assert.AreEqual("0000" + "0100" + "0001" + "0000", Convert.ToHexString(buf, 2, 8));
        var body = buf[10..];
        Assert.AreEqual(declared, body.Length);
        Assert.AreEqual(0, body.Length % 16);
        Assert.AreEqual(value, TestCrypto.TrimZeros(TestCrypto.EcbDecrypt(key, body)));

        var plain = P2PCodec.BuildStringCommandPayload(value, 2);
        Assert.AreEqual(0x02, plain[6]);
        Assert.AreEqual(0x00, plain[7]);
        Assert.AreEqual(value, Encoding.UTF8.GetString(plain, 10, plain.Length - 10));
    }

    [TestMethod]
    public void PadsWithZerosNotPkcs7AndRoundTripsEcb()
    {
        Assert.HasCount(16, P2PCodec.PaddingP2PData([]));
        Assert.HasCount(16, P2PCodec.PaddingP2PData(new byte[16]));
        var padded = P2PCodec.PaddingP2PData(new byte[17]);
        Assert.HasCount(32, padded);
        CollectionAssert.AreEqual(new byte[15], padded[17..]);
        var key = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        CollectionAssert.AreEqual(padded, P2PCodec.DecryptP2PData(P2PCodec.EncryptP2PData(padded, key), key));
        CollectionAssert.AreEqual(TestCrypto.EcbEncrypt(key, padded), P2PCodec.EncryptP2PData(padded, key));
    }

    [TestMethod]
    public void FramesARawCommandWithItsSignCodeMagicAndStream()
    {
        var raw = P2PCodec.BuildRawCommandPayload([0xaa, 0xbb, 0xcc], 6, 8, 0x08, 0x00, 0x0b);
        Assert.AreEqual("0300" + "0000" + "0800" + "0608" + "0B00" + "AABBCC", Convert.ToHexString(raw));
        Assert.AreEqual("0000" + "0000" + "0100" + "0000" + "0000", Convert.ToHexString(P2PCodec.BuildRawCommandPayload([])));
    }

    [TestMethod]
    public void BuildsTheAckAndParsesTheDataHeader()
    {
        Assert.AreEqual("D1010001ABCD", Convert.ToHexString(P2PCodec.BuildAckPayload(P2PDataType.Video, 0xabcd)));
        byte[] frame = [.. "XZYH"u8, 0x46, 0x05, 0x10, 0x01, 0x00, 0x00, 0x01, 0x00, 0x03, 0x08, 0x0a, 0x00];
        Assert.AreEqual(new P2PDataFrameHeader(1350, 0x110, 3, 8, 10), P2PCodec.ParseDataFrameHeader(frame));
    }

    [TestMethod]
    public void ReadsANulTerminatedString()
    {
        Assert.AreEqual("{\"a\":1}", P2PCodec.ReadNullTerminatedString([.. "{\"a\":1}"u8, 0, 0x41]));
        Assert.AreEqual("abc", P2PCodec.ReadNullTerminatedString("abc"u8));
    }

    [TestMethod]
    public void BuildsTheDirectBinaryBodies()
    {
        var station = P2PWriteCommands.BuildDirectBinaryBody(63, "acct");
        Assert.HasCount(132, station);
        Assert.AreEqual("3F000000" + "61636374", Convert.ToHexString(station, 0, 8));
        CollectionAssert.AreEqual(new byte[124], station[8..]);

        var device = P2PWriteCommands.BuildDirectBinaryBody(1, "acct", channel: 6);
        Assert.HasCount(136, device);
        Assert.AreEqual("06000000" + "01000000" + "61636374", Convert.ToHexString(device, 0, 12));

        Assert.HasCount(132, P2PWriteCommands.BuildDirectBinaryBody(0, new string('x', 200)));
        Assert.ThrowsExactly<ArgumentException>(() => P2PWriteCommands.BuildDirectBinaryBody(0, ""));
    }
}
