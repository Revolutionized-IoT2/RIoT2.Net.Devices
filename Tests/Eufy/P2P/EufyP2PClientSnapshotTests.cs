using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RIoT2.Net.Devices.Services.Eufy;
using RIoT2.Net.Devices.Services.Eufy.P2P;

namespace RIoT2.Net.Devices.Tests.Eufy.P2P;

/// <summary>
/// <see cref="EufyP2PClient.SnapshotAsync"/> against a fake station that streams Annex-B video once its live start
/// arrives: own-session and HomeBase-attached starts, the key-frame gate, stop, timeout and session lifetime. The
/// ffmpeg decode is replaced by a recorder; the ffmpeg invocation itself is <see cref="FfmpegTests"/>.
/// </summary>
[TestClass]
public class EufyP2PClientSnapshotTests
{
    private const string CameraSn = "T8113P0000000001";
    private const string BaseSn = "T8010P0000000001";
    private static readonly byte[] Jpeg = [0xff, 0xd8, 0xff, 0xd9];

    private static readonly byte[] Sps = [0x67, 0x42, 0x00, 0x1e];
    private static readonly byte[] Pps = [0x68, 0xce, 0x3c, 0x80];

    private sealed class Decodes
    {
        private readonly List<byte[]> bursts = [];

        public IReadOnlyList<byte[]> Bursts
        {
            get { lock (bursts) return [.. bursts]; }
        }

        public Task<byte[]> Decode(byte[] burst, CancellationToken cancellationToken)
        {
            lock (bursts) bursts.Add(burst);
            return Task.FromResult(Jpeg);
        }
    }

    /// <summary>A delta frame, then two keyframes told apart by their IDR's last byte, on the camera's channel.</summary>
    private static void StreamTwoKeyframes(FakeStation station, StationCommand start, int channel)
    {
        void Send(byte[] annexb, bool key, int seq) => station.SendData(start.From, 1300, channel, 0, FakeStation.VideoPayload(annexb, key, seq, (uint)(seq * 40)), dataType: 1);
        Send(AnnexBTests.Unit([0x21, 0x9a, 0x00]), false, 1);
        Send(AnnexBTests.Unit(Sps, Pps, [0x65, 0x88, 0x01]), true, 2);
        Send(AnnexBTests.Unit([0x21, 0x9a, 0x01]), false, 3);
        Send(AnnexBTests.Unit(Sps, Pps, [0x65, 0x88, 0x02]), true, 4);
    }

    [TestMethod]
    public async Task CapturesTheSecondKeyframeOfAnOwnSessionCameraAndStopsTheStream()
    {
        var decodes = new Decodes();
        var started = 0;
        await using var rig = new P2PRig(CameraSn, [P2PDevices.Record(CameraSn, "T8113", did: P2PRig.Did)], s => s.OnCommand = (station, c) =>
        {
            if (c.Cmd == 1700 && Interlocked.Increment(ref started) == 1) StreamTwoKeyframes(station, c, 0);
        }, s => P2PRig.With(s, decoder: decodes.Decode));

        var jpeg = await rig.Client.SnapshotAsync(CameraSn, CancellationToken.None).Bounded();

        CollectionAssert.AreEqual(Jpeg, jpeg);
        var burst = decodes.Bursts.Single();
        CollectionAssert.AreEqual(new[] { 0x67, 0x68, 0x67, 0x68, 0x65 }, AnnexB.SplitNals(burst).Select(n => (int)n[0]).ToArray(), "the stream's sets are re-emitted ahead of the unit");
        Assert.AreEqual(0x02, burst[^1], "the first keyframe of a cold stream is skipped");

        var start = rig.Station.CommandsOf(1700)[0];
        Assert.AreEqual(1, start.SignCode, "a session with no level-2 key starts at level 1");
        Assert.AreEqual(11, start.StreamId);
        var json = start.Json;
        Assert.AreEqual(1000, json.GetProperty("commandType").GetInt32());
        var data = json.GetProperty("data");
        Assert.AreEqual("admin-0001", data.GetProperty("account_id").GetString());
        Assert.AreEqual(0, data.GetProperty("mChannel").GetInt32());
        Assert.AreEqual(2, data.GetProperty("streamtype").GetInt32());
        Assert.AreEqual(256, data.GetProperty("encryptkey").GetString()!.Length);

        await AsyncTestExtensions.Until(() => rig.Station.CommandsOf(1004).Count == 1, "the stop");
        Assert.AreEqual(0, rig.Station.CommandsOf(1004)[0].Channel);
        Assert.AreEqual(0, rig.Station.Ends, "a camera's session outlives the snapshot until it idles");

        await rig.Client.CloseAsync().Bounded();
        Assert.AreEqual(1, rig.Station.Ends);
    }

    [TestMethod]
    public async Task StartsAnAttachedCameraOnTheBaseSessionAtLevel2AndReusesTheSession()
    {
        using var cipher = TestCrypto.NewP256();
        var decodes = new Decodes();
        IReadOnlyList<EufyDeviceRecord> devices =
        [
            P2PDevices.Record(BaseSn, "T8010", did: P2PRig.Did, deviceType: 0),
            P2PDevices.Record(CameraSn, "T8113", stationSn: BaseSn, channel: 3),
            P2PDevices.Record("T8113P0000000002", "T8113", stationSn: BaseSn, channel: 4),
        ];
        await using var rig = new P2PRig(BaseSn, devices, s =>
        {
            s.Cipher = cipher;
            s.OnCommand = (station, c) =>
            {
                if (c.Cmd != 1350 || c.SignCode != 8) return;
                var json = c.Json;
                if (json.GetProperty("cmd").GetInt32() != 1003) return;
                var channel = json.GetProperty("mChannel").GetInt32();
                station.SendData(c.From, 1300, 9, 0, FakeStation.VideoPayload(AnnexBTests.Unit(Sps, Pps, [0x65, 0x88, 0x09]), true, 1, 1), dataType: 1);
                StreamTwoKeyframes(station, c, channel);
            };
        }, s => P2PRig.With(s, level2Grace: TimeSpan.FromSeconds(10), decoder: decodes.Decode));

        CollectionAssert.AreEqual(Jpeg, await rig.Client.SnapshotAsync(CameraSn, CancellationToken.None).Bounded());
        Assert.AreEqual(0x02, decodes.Bursts.Single()[^1], "a frame of another channel is not this camera's");

        var start = rig.Station.CommandsOf(1350).First(c => c.Json.GetProperty("cmd").GetInt32() == 1003);
        Assert.AreEqual(3, start.Channel);
        Assert.AreEqual((byte)0x08, start.Magic0);
        var json = start.Json;
        Assert.AreEqual("admin-0001", json.GetProperty("account_id").GetString());
        Assert.AreEqual(3, json.GetProperty("mChannel").GetInt32());
        Assert.AreEqual(1003, json.GetProperty("mValue3").GetInt32());
        var payload = json.GetProperty("payload");
        Assert.AreEqual("Android", payload.GetProperty("ClientOS").GetString());
        Assert.AreEqual("admin-0001", payload.GetProperty("accountId").GetString());
        Assert.AreEqual(2, payload.GetProperty("streamtype").GetInt32());
        Assert.AreEqual(256, payload.GetProperty("key").GetString()!.Length);
        await AsyncTestExtensions.Until(() => rig.Station.CommandsOf(1350).Any(c => c.Json.GetProperty("cmd").GetInt32() == 1004), "the attached stop");

        // A second snapshot rides the same (wired, kept-open) base session.
        CollectionAssert.AreEqual(Jpeg, await rig.Client.SnapshotAsync(CameraSn, CancellationToken.None).Bounded());
        Assert.AreEqual(1, rig.Station.CommandsOf(1100).Select(c => c.From).Distinct().Count());
        Assert.AreEqual(0, rig.Station.Ends);

        await rig.Client.DisposeAsync();
        Assert.AreEqual(1, rig.Station.Ends);
    }

    [TestMethod]
    public async Task GivesUpAfterTheSnapshotTimeoutAndStopsTheStream()
    {
        await using var rig = new P2PRig(CameraSn, [P2PDevices.Record(CameraSn, "T8113", did: P2PRig.Did)], configure: s => P2PRig.With(s, snapshotTimeout: TimeSpan.FromMilliseconds(500), decoder: new Decodes().Decode));
        var error = await Assert.ThrowsExactlyAsync<TimeoutException>(() => rig.Client.SnapshotAsync(CameraSn, CancellationToken.None).Bounded());
        StringAssert.Contains(error.Message, CameraSn);
        await AsyncTestExtensions.Until(() => rig.Station.CommandsOf(1004).Count == 1, "the stop");
    }

    [TestMethod]
    public async Task HonoursTheCallersCancellation()
    {
        await using var rig = new P2PRig(CameraSn, [P2PDevices.Record(CameraSn, "T8113", did: P2PRig.Did)], configure: s => P2PRig.With(s, decoder: new Decodes().Decode));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAsync<OperationCanceledException>(() => rig.Client.SnapshotAsync(CameraSn, cts.Token).Bounded());
    }

    [TestMethod]
    public async Task ABatteryCamerasSessionClosesOnceIdleAndReopensOnDemand()
    {
        var decodes = new Decodes();
        await using var rig = new P2PRig(CameraSn, [P2PDevices.Record(CameraSn, "T8113", did: P2PRig.Did)], s => s.OnCommand = (station, c) =>
        {
            if (c.Cmd == 1700) StreamTwoKeyframes(station, c, 0);
        }, s => P2PRig.With(s, decoder: decodes.Decode, batteryIdle: TimeSpan.FromMilliseconds(300)));

        await rig.Client.SnapshotAsync(CameraSn, CancellationToken.None).Bounded();
        await AsyncTestExtensions.Until(() => rig.Station.Ends == 1, "the idle close");

        await rig.Client.SnapshotAsync(CameraSn, CancellationToken.None).Bounded();
        Assert.HasCount(2, decodes.Bursts);
        Assert.AreEqual(2, rig.Station.CommandsOf(1100).Select(c => c.From).Distinct().Count(), "the second snapshot opened a new session");
    }

    [TestMethod]
    public async Task RefusesWhatItCannotStream()
    {
        IReadOnlyList<EufyDeviceRecord> devices =
        [
            P2PDevices.Record("T9000P0000000001", "T8030", did: P2PRig.Did, deviceType: 27),
            P2PDevices.Record("T8113P0000000009", "T8113", stationSn: "T9000P0000000001", channel: 1),
            P2PDevices.Record(BaseSn, "T8010", did: P2PRig.Did),
            P2PDevices.Record("CAMA", "T8113", stationSn: BaseSn, channel: 2),
            P2PDevices.Record("CAMB", "T8113", stationSn: BaseSn, channel: 2),
        ];
        await using var rig = new P2PRig(BaseSn, devices, configure: s => P2PRig.With(s, decoder: new Decodes().Decode));
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => rig.Client.SnapshotAsync("T8113P0000000009", CancellationToken.None));
        var shared = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => rig.Client.SnapshotAsync("CAMA", CancellationToken.None));
        StringAssert.Contains(shared.Message, "shared");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => rig.Client.SnapshotAsync("unknown", CancellationToken.None));
        Assert.IsEmpty(rig.Network.Sockets);
    }
}
