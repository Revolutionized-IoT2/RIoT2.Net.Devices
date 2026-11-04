// Ported from eufy-sdk src/transport/http/__tests__/decode_v1.spec.ts, decode_v2.spec.ts, decode_v2_dri.spec.ts,
// jpeg_scan.spec.ts @ 5f92bea5 (Apache-2.0), modified for RIoT2.

using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RIoT2.Net.Devices.Services.Eufy.Cloud;

namespace RIoT2.Net.Devices.Tests.Eufy.Cloud;

[TestClass]
public class PushImageDecoderTests
{
    private const string Serial = "T8010P1234567890";
    private const string Code = "AB12345678";
    private const string P2pDid = "ABCDEF-123456-ABCDE";

    private static byte[] Original() =>
    [
        0xff, 0xd8, 0xff, 0xe0,
        .. Enumerable.Repeat((byte)0x7a, 252),
        .. Encoding.ASCII.GetBytes("PLAINTEXT-TAIL-SCAN-DATA"),
        0xff, 0xd9,
    ];

    /// <summary>A v1 blob whose first 256 DATA bytes are AES-128-ECB encrypted (no padding).</summary>
    private static byte[] MakeV1Blob(byte[] plaintext)
    {
        using var aes = Aes.Create();
        aes.Key = Encoding.UTF8.GetBytes(PushImageDecoder.GetImageKey(Serial, P2pDid, Code))[..16];
        var head = aes.EncryptEcb(plaintext.AsSpan(0, 256), PaddingMode.None);
        return [.. Encoding.Latin1.GetBytes($"{PushImageDecoder.V1Prefix}:{Serial}:{Code}:"), .. head, .. plaintext.AsSpan(256)];
    }

    private static (int Width, int Height) JpegSize(byte[] jpeg)
    {
        var sof = Find(jpeg, 0xff, 0xc0);
        return ((jpeg[sof + 7] << 8) | jpeg[sof + 8], (jpeg[sof + 5] << 8) | jpeg[sof + 6]);
    }

    private static (int Min, int Max, double Mean)[] Channels(DecodedJpeg image)
    {
        var n = image.Width * image.Height;
        return [.. Enumerable.Range(0, 3).Select(channel =>
        {
            int min = 255, max = 0;
            double sum = 0;
            for (var i = 0; i < n; i++)
            {
                var v = image.Rgb[i * 3 + channel];
                min = Math.Min(min, v);
                max = Math.Max(max, v);
                sum += v;
            }
            return (min, max, sum / n);
        })];
    }

    private static DecodedJpeg OriginalPicture(byte[] blob) =>
        BaselineJpegDecoder.Decode(blob[Find(blob, 0xff, 0xd8)..]) ?? throw new AssertFailedException("the fixture head does not decode");

    [TestMethod]
    public void V1KeyDerivationMatchesUpstream()
    {
        // Reference values computed with eufy-sdk's decodeImageV1.ts.
        Assert.AreEqual(17, PushImageDecoder.GetIdSuffix(P2pDid));
        Assert.AreEqual("3F27BEB02C420A1C9299A749F350C136", PushImageDecoder.GetImageKey(Serial, P2pDid, Code));
        Assert.AreEqual("07297A415A10697F2261DF07C16CB501", PushImageDecoder.GetImageKey("T8010P123456789Z", "ABCDEF-12-ABCDE", "XX00000042"));
    }

    [TestMethod]
    public void V1DetectsAndRoundTrips()
    {
        var original = Original();
        var blob = MakeV1Blob(original);
        Assert.IsTrue(PushImageDecoder.IsV1Image(blob));
        Assert.IsFalse(PushImageDecoder.IsV1Image(Encoding.ASCII.GetBytes("v2_eufysecurity:T8:1:x")));
        Assert.IsFalse(PushImageDecoder.IsV1Image([0xff, 0xd8, 0xff]));

        var decoded = PushImageDecoder.DecodeImageV1(blob, P2pDid);
        CollectionAssert.AreEqual(original, decoded);
        Assert.IsNull(PushImageDecoder.DecodeImageV1(Encoding.ASCII.GetBytes("v2_eufysecurity:x"), P2pDid));
    }

    [TestMethod]
    public void NormalizesV1AndLeavesUnsupportedMediaAlone()
    {
        var original = Original();
        var blob = MakeV1Blob(original);
        var unsupported = Encoding.ASCII.GetBytes("v2_eufysecurity:x");
        var plain = new byte[] { 0xff, 0xd8, 0xff, 0xe0, 0x00, 0x10 };

        CollectionAssert.AreEqual(original, PushImageDecoder.NormalizePushImage(blob, P2pDid));
        Assert.AreSame(blob, PushImageDecoder.NormalizePushImage(blob));
        Assert.AreSame(unsupported, PushImageDecoder.NormalizePushImage(unsupported, P2pDid));
        Assert.AreSame(plain, PushImageDecoder.NormalizePushImage(plain));
    }

    [TestMethod]
    public void V2RecognisesItsPrefixOnly()
    {
        Assert.IsTrue(PushImageDecoderV2.IsV2Image(EufyImageFixtures.Thumbnail176x144));
        Assert.IsFalse(PushImageDecoderV2.IsV2Image(Encoding.ASCII.GetBytes("eufysecurity:T8:CODE:xxxx")));
        Assert.IsFalse(PushImageDecoderV2.IsV2Image([0xff, 0xd8, 0xff, 0xe0]));
        Assert.IsFalse(PushImageDecoderV2.IsV2Image(Encoding.ASCII.GetBytes("v2_eufysecurityX:garbage")));
    }

    [TestMethod]
    public void V2ReconstructsTheOriginalGeometry()
    {
        var jpeg = PushImageDecoderV2.DecodeImageV2(EufyImageFixtures.Thumbnail176x144);
        Assert.IsNotNull(jpeg);
        CollectionAssert.AreEqual(new byte[] { 0xff, 0xd8 }, jpeg[..2]);
        Assert.AreEqual((176, 144), JpegSize(jpeg));

        var offLadder = PushImageDecoderV2.DecodeImageV2(EufyImageFixtures.Thumbnail264x200);
        Assert.IsNotNull(offLadder);
        CollectionAssert.AreEqual(new byte[] { 0xff, 0xd9 }, offLadder[^2..]);
        Assert.AreEqual((264, 200), JpegSize(offLadder));
    }

    [TestMethod]
    public void V2RecoversYuv420Subsampling()
    {
        var jpeg = PushImageDecoderV2.DecodeImageV2(EufyImageFixtures.Thumbnail320x240Yuv420);
        Assert.IsNotNull(jpeg);
        Assert.AreEqual((320, 240), JpegSize(jpeg));
        var sof = Find(jpeg, 0xff, 0xc0);
        Assert.AreEqual(0x22, jpeg[sof + 11]);
    }

    [TestMethod]
    public void V2AnswersNullWhenThereIsNothingToSplice()
    {
        Assert.IsNull(PushImageDecoderV2.DecodeImageV2(Encoding.ASCII.GetBytes("not a v2 image")));
        Assert.IsNull(PushImageDecoderV2.DecodeImageV2(Encoding.ASCII.GetBytes("v2_eufysecurity:T8000TEST00000002:0000000000:no jpeg here")));
        var blob = EufyImageFixtures.Thumbnail176x144;
        Assert.IsNull(PushImageDecoderV2.DecodeImageV2(blob[..^400]));
    }

    [TestMethod]
    public void NormalizeRoutesV2ThroughTheDecoder()
    {
        var output = PushImageDecoder.NormalizePushImage(EufyImageFixtures.Thumbnail176x144);
        CollectionAssert.AreEqual(new byte[] { 0xff, 0xd8 }, output[..2]);
        Assert.AreEqual((176, 144), JpegSize(output));
    }

    [TestMethod]
    [DataRow(nameof(EufyImageFixtures.Thumbnail176x144), 176, 144)]
    [DataRow(nameof(EufyImageFixtures.Thumbnail264x200), 264, 200)]
    [DataRow(nameof(EufyImageFixtures.Thumbnail320x240Yuv420), 320, 240)]
    public void V2ReproducesTheSourceTones(string fixture, int width, int height)
    {
        var blob = Fixture(fixture);
        var output = PushImageDecoderV2.DecodeImageV2(blob);
        Assert.IsNotNull(output);
        var got = BaselineJpegDecoder.Decode(output);
        Assert.IsNotNull(got);
        Assert.AreEqual((width, height), (got.Width, got.Height));

        var before = Channels(OriginalPicture(blob));
        var after = Channels(got);
        for (var channel = 0; channel < 3; channel++)
        {
            Assert.AreEqual(before[channel].Mean, after[channel].Mean, 0.5);
            Assert.IsLessThanOrEqualTo(8, after[channel].Min);
            Assert.IsGreaterThanOrEqualTo(247, after[channel].Max);
        }
    }

    [TestMethod]
    public void V2OpensALowQualityThumbnailBackUp()
    {
        var blob = EufyImageFixtures.Thumbnail176x144Q30;
        var output = PushImageDecoderV2.DecodeImageV2(blob);
        Assert.IsNotNull(output);

        var before = Channels(OriginalPicture(blob));
        var after = Channels(BaselineJpegDecoder.Decode(output)!);
        for (var channel = 0; channel < 3; channel++)
        {
            Assert.IsGreaterThan(240, after[channel].Max - after[channel].Min);
            Assert.IsLessThan(2, Math.Abs(after[channel].Mean - before[channel].Mean));
        }
    }

    [TestMethod]
    public void V2LeavesAFullRangeThumbnailAtTheReferenceQuality()
    {
        var jpeg = PushImageDecoderV2.DecodeImageV2(EufyImageFixtures.Thumbnail176x144)!;
        var dqt = Find(jpeg, 0xff, 0xdb);
        Assert.AreEqual(5, jpeg[dqt + 5]);
    }

    /// <summary>A blob cut at a DRI in front of the SOS: the chroma tables went with the encrypted prefix.</summary>
    [TestMethod]
    public void V2ReconstructsFromATailThatStartsAtDri()
    {
        byte[] driZero = [0xff, 0xdd, 0x00, 0x04, 0x00, 0x00];
        byte[] sos = [0xff, 0xda, 0x00, 0x0c];
        var source = EufyImageFixtures.Thumbnail176x144;
        var scan = source[source.AsSpan().LastIndexOf(sos)..];
        byte[] Blob(byte[] prefix) => [.. Encoding.ASCII.GetBytes("v2_eufysecurity:"), .. prefix, .. driZero, .. scan];

        var output = PushImageDecoderV2.DecodeImageV2(Blob(Garbage(600, 1)));
        Assert.IsNotNull(output);
        Assert.IsGreaterThanOrEqualTo(0, Find(output, [.. driZero, .. sos]));
        var image = BaselineJpegDecoder.Decode(output);
        Assert.IsNotNull(image);
        Assert.AreEqual((176, 144), (image.Width, image.Height));

        byte[] decoy = [.. Garbage(200, 2), 0xff, 0xdd, .. Garbage(100, 3), .. driZero, .. Garbage(50, 4), .. driZero, 0xff, 0xda, 0x00, 0x0c, 0x03, .. Garbage(200, 5)];
        var decoyed = PushImageDecoderV2.DecodeImageV2(Blob(decoy));
        Assert.IsNotNull(decoyed);
        Assert.AreEqual((176, 144), JpegSize(decoyed));

        var broken = Blob(Garbage(600, 1));
        broken[broken.AsSpan().LastIndexOf(sos) + 4] = 0x01;
        Assert.IsNull(PushImageDecoderV2.DecodeImageV2(broken));
    }

    [TestMethod]
    public void ScanCountsTheMcusOfOneSubsamplingOnly()
    {
        var tail444 = Tail(EufyImageFixtures.Thumbnail176x144);
        var scan = JpegScan.ScanEntropy(tail444, 0, LumaTables);
        Assert.IsNotNull(scan);
        Assert.AreEqual(396, scan.Mcus);
        Assert.IsTrue(scan.Complete);
        Assert.IsFalse(JpegScan.ScanEntropy(tail444, 1, LumaTables)!.Complete);
        Assert.IsFalse(JpegScan.ScanEntropy(tail444, 2, LumaTables)!.Complete);
        Assert.HasCount(scan.Mcus, scan.Luma);
        Assert.IsGreaterThan(10, scan.Luma.Distinct().Count());

        var tail420 = Tail(EufyImageFixtures.Thumbnail320x240Yuv420);
        var scan420 = JpegScan.ScanEntropy(tail420, 2, LumaTables);
        Assert.AreEqual(300, scan420!.Mcus);
        Assert.IsTrue(scan420.Complete);
        Assert.IsFalse(JpegScan.ScanEntropy(tail420, 0, LumaTables)!.Complete);
        Assert.IsFalse(JpegScan.ScanEntropy(tail420, 1, LumaTables)!.Complete);
    }

    [TestMethod]
    public void ScanStopsWhereATruncatedScanStops()
    {
        var blob = EufyImageFixtures.Thumbnail176x144;
        var cut = JpegScan.ScanEntropy(Tail(blob[..^400]), 0, LumaTables);
        Assert.IsNotNull(cut);
        Assert.IsFalse(cut.Complete);
        Assert.IsGreaterThan(0, cut.Mcus);
        Assert.IsLessThan(396, cut.Mcus);

        Assert.IsNull(JpegScan.ScanEntropy(Encoding.ASCII.GetBytes("no jpeg here at all"), 0, LumaTables));
        Assert.IsNull(JpegScan.ScanEntropy(Tail(blob), 0, []));
    }

    private static int Find(byte[] data, params byte[] pattern) => data.AsSpan().IndexOf(pattern);

    private static readonly byte[][] LumaTables = [PushImageDecoderV2.DhtDcLuma, PushImageDecoderV2.DhtAcLuma];

    private static byte[] Tail(byte[] blob) => blob[blob.AsSpan().IndexOf(PushImageDecoderV2.DcChroma)..];

    private static byte[] Fixture(string name) => name switch
    {
        nameof(EufyImageFixtures.Thumbnail176x144) => EufyImageFixtures.Thumbnail176x144,
        nameof(EufyImageFixtures.Thumbnail264x200) => EufyImageFixtures.Thumbnail264x200,
        nameof(EufyImageFixtures.Thumbnail320x240Yuv420) => EufyImageFixtures.Thumbnail320x240Yuv420,
        _ => EufyImageFixtures.Thumbnail176x144Q30,
    };

    /// <summary>Seeded bytes standing in for the encrypted prefix; never the DC-chroma marker.</summary>
    private static byte[] Garbage(int length, uint seed)
    {
        var output = new byte[length];
        var state = seed;
        for (var i = 0; i < length; i++)
        {
            state = unchecked(state * 1664525u + 1013904223u);
            output[i] = (byte)(state >> 24);
        }
        Assert.AreEqual(-1, output.AsSpan().IndexOf(PushImageDecoderV2.DcChroma));
        return output;
    }
}
