using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RIoT2.Net.Devices.Services.Eufy;
using RIoT2.Net.Devices.Services.Eufy.P2P;

namespace RIoT2.Net.Devices.Tests.Eufy.P2P;

/// <summary>The ffmpeg invocation (port of eufy-sdk <c>transport/ffmpeg.ts</c> and the decode of <c>p2p/media.ts</c>).</summary>
[TestClass]
public class FfmpegTests
{
    private static string MissingPath() => Path.Combine(AppContext.BaseDirectory, "no-such-dir-" + Guid.NewGuid().ToString("N"), OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");

    [TestMethod]
    public void DecodesOneAnnexBFrameToAJpegOnStdout()
    {
        CollectionAssert.AreEqual(
            new[] { "-hide_banner", "-loglevel", "error", "-f", "h264", "-i", "pipe:0", "-frames:v", "1", "-pix_fmt", "yuvj420p", "-f", "image2", "-vcodec", "mjpeg", "pipe:1" },
            Ffmpeg.SnapshotArguments(VideoCodec.H264).ToArray());
        Assert.AreEqual("hevc", Ffmpeg.SnapshotArguments(VideoCodec.H265)[4]);
    }

    [TestMethod]
    public void ABlankPathMeansTheBareNameAndAnyOtherIsUsedVerbatim()
    {
        Assert.AreEqual("ffmpeg", Ffmpeg.Executable(null));
        Assert.AreEqual("ffmpeg", Ffmpeg.Executable(""));
        Assert.AreEqual("ffmpeg", Ffmpeg.Executable("   "));
        Assert.AreEqual(" /opt/ff mpeg ", Ffmpeg.Executable(" /opt/ff mpeg "));
    }

    [TestMethod]
    public void LocatesAnExplicitPathOnlyWhenTheFileExists()
    {
        Assert.IsNull(Ffmpeg.Locate(MissingPath()));
        var existing = typeof(FfmpegTests).Assembly.Location;
        Assert.AreEqual(Path.GetFullPath(existing), Ffmpeg.Locate(existing));
    }

    [TestMethod]
    public async Task AMissingExecutableIsReportedAsNotFound()
    {
        var path = MissingPath();
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            Ffmpeg.AnnexBToJpegAsync(AnnexBTests.Unit([0x67, 0x42], [0x68, 0xce], [0x65, 0x88]), path, NullLogger.Instance, CancellationToken.None).Bounded());
        StringAssert.StartsWith(error.Message, $"ffmpeg not found at '{path}'");
    }

    [TestMethod]
    public async Task SnapshotRefusesUpFrontWhenFfmpegIsMissing()
    {
        var path = MissingPath();
        var network = new FakeP2PNetwork();
        var options = new EufyAccountOptions { Email = "user@example.invalid", Password = "x", FfmpegPath = path };
        await using var client = new EufyP2PClient(options, new FakeP2PCloud(), NullLogger.Instance, new EufyP2PClientSettings { Transport = network });
        client.UpdateDevices([P2PDevices.Record("T8113P0000000001", "T8113", did: "EUPRCAM-000000-XXXXX")]);

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => client.SnapshotAsync("T8113P0000000001", CancellationToken.None).Bounded());
        StringAssert.StartsWith(error.Message, $"ffmpeg not found at '{path}'");
        Assert.IsEmpty(network.Sockets, "no session is opened for a snapshot that cannot be decoded");
    }
}
