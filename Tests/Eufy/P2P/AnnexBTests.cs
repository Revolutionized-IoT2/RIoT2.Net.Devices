using Microsoft.VisualStudio.TestTools.UnitTesting;
using RIoT2.Net.Devices.Services.Eufy.P2P;

namespace RIoT2.Net.Devices.Tests.Eufy.P2P;

/// <summary>
/// Annex-B parsing and key-frame detection (port of eufy-sdk <c>annexb.spec.ts</c> and
/// <c>annexb-param-sets.spec.ts</c>), the snapshot's decodability gate and the access-unit assembler.
/// </summary>
[TestClass]
public class AnnexBTests
{
    private static readonly byte[] Sc4 = [0, 0, 0, 1];
    private static readonly byte[] Sc3 = [0, 0, 1];

    private static readonly byte[] H264Sps = [0x67, 0x42, 0x00, 0x1e];
    private static readonly byte[] H264Pps = [0x68, 0xce, 0x3c, 0x80];
    private static readonly byte[] H264Idr = [0x65, 0x88, 0x84];
    private static readonly byte[] H264P = [0x21, 0x9a, 0x00];

    private static readonly byte[] H265Vps = [0x40, 0x01, 0x0c];
    private static readonly byte[] H265Sps = [0x42, 0x01, 0x01];
    private static readonly byte[] H265Pps = [0x44, 0x01, 0xc0];
    private static readonly byte[] H265Idr = [0x26, 0x01, 0xaf];
    private static readonly byte[] H265Cra = [0x2a, 0x01, 0xaf];

    internal static byte[] Unit(params byte[][] nals) => [.. nals.SelectMany(n => Sc4.Concat(n))];

    private static int[] NalTypes(byte[] buf) => [.. AnnexB.SplitNals(buf).Select(n => (int)n[0])];

    [TestMethod]
    public void SniffsTheCodecFromItsParameterSets()
    {
        Assert.AreEqual(VideoCodec.H264, AnnexB.SniffCodec(Unit(H264Sps, H264Pps, H264Idr)));
        Assert.AreEqual(VideoCodec.H265, AnnexB.SniffCodec(Unit(H265Vps, H265Sps, H265Pps, H265Idr)));
        Assert.IsNull(AnnexB.SniffCodec(Unit(H264P)));
        Assert.AreEqual(VideoCodec.H264, AnnexB.SniffCodec([.. Sc3, .. H264Sps]));
        Assert.IsNull(AnnexB.SniffCodec([.. new byte[70], .. Sc4, .. H264Sps]), "only the first 64 bytes are scanned");
    }

    [TestMethod]
    public void ExtractsParameterSetBodies()
    {
        var h264 = AnnexB.ExtractParamSets(Unit(H264Sps, H264Pps, H264Idr));
        Assert.IsNotNull(h264);
        Assert.AreEqual(VideoCodec.H264, h264.Codec);
        CollectionAssert.AreEqual(H264Sps, h264.Sps.Single());
        CollectionAssert.AreEqual(H264Pps, h264.Pps.Single());
        Assert.IsEmpty(h264.Vps);

        var h265 = AnnexB.ExtractParamSets(Unit(H265Vps, H265Sps, H265Pps, H265Idr));
        Assert.IsNotNull(h265);
        Assert.AreEqual(VideoCodec.H265, h265.Codec);
        CollectionAssert.AreEqual(H265Vps, h265.Vps.Single());
        Assert.HasCount(1, h265.Sps);
        Assert.HasCount(1, h265.Pps);
        Assert.IsNull(AnnexB.ExtractParamSets(Unit(H264P)));
    }

    [TestMethod]
    public void DetectsIdrAndIrapPictures()
    {
        Assert.IsTrue(AnnexB.HasIdr(Unit(H264Sps, H264Idr), VideoCodec.H264));
        Assert.IsFalse(AnnexB.HasIdr(Unit(H264P), VideoCodec.H264));
        Assert.IsTrue(AnnexB.HasIdr(Unit(H265Sps, H265Idr), VideoCodec.H265));
        Assert.IsFalse(AnnexB.HasIdr(Unit(H265Cra), VideoCodec.H265));
        Assert.IsTrue(AnnexB.HasRandomAccessPicture(Unit(H265Cra), VideoCodec.H265), "a CRA (21) is an IRAP");
        Assert.IsFalse(AnnexB.HasRandomAccessPicture(Unit(H264P), VideoCodec.H264));
    }

    [TestMethod]
    public void AKeyFrameIsDecodableOnlyWithEveryParameterSetKnown()
    {
        var h264 = AnnexB.ExtractParamSets(Unit(H264Sps, H264Pps));
        Assert.IsTrue(AnnexB.IsDecodableKeyFrame(Unit(H264Sps, H264Pps, H264Idr), h264));
        Assert.IsTrue(AnnexB.IsDecodableKeyFrame(Unit(H264Idr), h264), "sets announced earlier in the stream count");
        Assert.IsFalse(AnnexB.IsDecodableKeyFrame(Unit(H264P), h264), "no IDR");
        Assert.IsFalse(AnnexB.IsDecodableKeyFrame(Unit(H264Idr), null), "no SPS/PPS yet");
        Assert.IsFalse(AnnexB.IsDecodableKeyFrame(Unit(H264Sps, H264Idr), AnnexB.ExtractParamSets(Unit(H264Sps))), "SPS without PPS");

        var h265 = AnnexB.ExtractParamSets(Unit(H265Vps, H265Sps, H265Pps));
        Assert.IsTrue(AnnexB.IsDecodableKeyFrame(Unit(H265Vps, H265Sps, H265Pps, H265Idr), h265));
        Assert.IsTrue(AnnexB.IsDecodableKeyFrame(Unit(H265Cra), h265));
        Assert.IsFalse(AnnexB.IsDecodableKeyFrame(Unit(H265Sps, H265Pps, H265Idr), AnnexB.ExtractParamSets(Unit(H265Sps, H265Pps))), "H.265 needs its VPS");
        Assert.IsFalse(AnnexB.IsDecodableKeyFrame(Unit(H265Vps, H265Sps, H265Pps, H265Idr), h264), "sets of another codec");
    }

    [TestMethod]
    public void PrefixesParameterSetsInStreamOrder()
    {
        var idr = Unit([0x65, 0x88, 0x84]);
        var h264 = AnnexB.ExtractParamSets(Unit([0x67, 0x42, 0x00], [0x68, 0xce, 0x01], [0x65, 0x88, 0x84]))!;
        var primed = AnnexB.PrefixParamSets(idr, h264);
        CollectionAssert.AreEqual(new[] { 0x67, 0x68, 0x65 }, NalTypes(primed));
        CollectionAssert.AreEqual(idr, primed[^idr.Length..]);

        var h265 = AnnexB.ExtractParamSets(Unit([0x40, 0x01, 0x0c], [0x42, 0x01, 0x01], [0x44, 0x01, 0xc1], [0x26, 0x01, 0xaf]))!;
        CollectionAssert.AreEqual(new[] { 0x40, 0x42, 0x44, 0x26 }, NalTypes(AnnexB.PrefixParamSets(Unit([0x26, 0x01, 0xaf]), h265)));

        CollectionAssert.AreEqual(idr, AnnexB.PrefixParamSets(idr, new ParamSets(VideoCodec.H264, [], [], [])));
        Assert.IsNull(AnnexB.ExtractParamSets(idr));
        var recovered = AnnexB.ExtractParamSets(primed)!;
        CollectionAssert.AreEqual(h264.Sps.Single(), recovered.Sps.Single());
        CollectionAssert.AreEqual(h264.Pps.Single(), recovered.Pps.Single());

        CollectionAssert.AreEqual(primed, LiveSnapshot.PrimeForDecode(idr, h264, VideoCodec.H264));
        CollectionAssert.AreEqual(idr, LiveSnapshot.PrimeForDecode(idr, h264, VideoCodec.H265), "sets of another codec are not used");
        CollectionAssert.AreEqual(idr, LiveSnapshot.PrimeForDecode(idr, null, VideoCodec.H264));
    }

    [TestMethod]
    public void FoldsReannouncedParameterSets()
    {
        var idr = Unit(H264Idr);
        var complete = AnnexB.ExtractParamSets(Unit(H264Sps, H264Pps, H264Idr))!;
        Assert.AreSame(complete, AnnexB.UpdatedParamSets(idr, complete));

        var spsOnly = AnnexB.UpdatedParamSets(Unit(H264Sps, H264Idr), complete)!;
        CollectionAssert.AreEqual(complete.Pps.Single(), spsOnly.Pps.Single());

        byte[] changed = [0x67, 0x4d, 0x00];
        CollectionAssert.AreEqual(changed, AnnexB.UpdatedParamSets(Unit(changed, H264Idr), complete)!.Sps.Single());

        var h265 = AnnexB.UpdatedParamSets(Unit(H265Vps, H265Sps, H265Pps, H265Idr), complete)!;
        Assert.AreEqual(VideoCodec.H265, h265.Codec);
        CollectionAssert.AreEqual(H265Sps, h265.Sps.Single());
        Assert.IsNull(AnnexB.UpdatedParamSets(idr, null));
    }

    [TestMethod]
    public void JoinsTheChunksOfALargeAccessUnit()
    {
        var dropped = new List<int>();
        var assembler = new AccessUnitAssembler((carried, _, _) => dropped.Add(carried));
        static byte[] Decode(byte[] payload) => payload[22..];

        var head = Unit(H264Sps, H264Pps, H264Idr);
        var first = FakeStation.VideoPayload(head, keyframe: true, sequence: 4, timestamp: 1000);
        first[0] = 0x00; first[1] = 0xfa; first[2] = 0x00; first[3] = 0x00; // declares 64000: continues
        Assert.IsEmpty(assembler.Push(first, Decode));
        var tail = FakeStation.VideoPayload([0x11, 0x22], keyframe: true, sequence: 4, timestamp: 1000);
        var unit = assembler.Push(tail, Decode).Single();
        Assert.IsTrue(unit.Keyframe);
        Assert.AreEqual(1920, unit.Width);
        Assert.AreEqual(1080, unit.Height);
        Assert.AreEqual(1000u, unit.SourceTimestamp);
        CollectionAssert.AreEqual((byte[])[.. head, 0x11, 0x22], unit.Data);

        Assert.IsEmpty(assembler.Push(first, Decode));
        Assert.HasCount(1, assembler.Push(FakeStation.VideoPayload(Unit(H264P), false, 5, 1040), Decode));
        CollectionAssert.AreEqual(new[] { head.Length }, dropped, "a unit whose tail never arrived is dropped");
        Assert.IsEmpty(assembler.Push(FakeStation.VideoPayload([0x11, 0x22], false, 6, 1080), Decode), "a body that starts no access unit");
        Assert.IsNull(VideoFrames.ParseHeader(new byte[21]));
    }

    [TestMethod]
    public void ReadsTheJpegGeometryFromItsFrameHeader()
    {
        byte[] jpeg = [0xff, 0xd8, 0xff, 0xe0, 0x00, 0x04, 0x00, 0x00, 0xff, 0xff, 0xc4, 0x00, 0x02, 0xff, 0xc0, 0x00, 0x0b, 0x08, 0x02, 0xd0, 0x05, 0x00, 0x01, 0x01, 0x11, 0x00, 0xff, 0xd9];
        Assert.AreEqual((1280, 720), Ffmpeg.JpegGeometry(jpeg));
        Assert.IsNull(Ffmpeg.JpegGeometry([0xff, 0xd8, 0xff, 0xd9]));
        Assert.IsNull(Ffmpeg.JpegGeometry([0xff, 0xd8, 0xff, 0xc0, 0x00]));
    }
}
