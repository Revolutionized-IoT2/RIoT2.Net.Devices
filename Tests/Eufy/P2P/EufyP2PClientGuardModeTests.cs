using System.Buffers.Binary;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RIoT2.Net.Devices.Services.Eufy;
using RIoT2.Net.Devices.Services.Eufy.P2P;

namespace RIoT2.Net.Devices.Tests.Eufy.P2P;

/// <summary>A client, its fake station and cloud.</summary>
internal sealed class P2PRig : IAsyncDisposable
{
    public const string Did = "EUPRCAM-000000-XXXXX";

    public P2PRig(string stationSn, IReadOnlyList<EufyDeviceRecord> devices, Action<FakeStation>? setup = null, Func<EufyP2PClientSettings, EufyP2PClientSettings>? configure = null, string? ffmpegPath = null)
    {
        Station = new FakeStation(stationSn, Did);
        setup?.Invoke(Station);
        Network = new FakeP2PNetwork { Station = Station };
        if (Station.Cipher is not null) Cloud.EccPrivateKeyHex = TestCrypto.PrivateKeyHex(Station.Cipher);
        var settings = new EufyP2PClientSettings
        {
            Transport = Network,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            Level2Grace = TimeSpan.FromMilliseconds(300),
            Level2Settle = TimeSpan.FromMilliseconds(300),
            DirectCommandInterval = TimeSpan.FromMilliseconds(1),
            ResultGrace = TimeSpan.FromMilliseconds(300),
            NoBroadcast = false,
        };
        settings = configure?.Invoke(settings) ?? settings;
        var options = new EufyAccountOptions { Email = "user@example.invalid", Password = "x", FfmpegPath = ffmpegPath ?? "ffmpeg" };
        Client = new EufyP2PClient(options, Cloud, NullLogger.Instance, settings);
        Client.UpdateDevices(devices);
    }

    public FakeStation Station { get; }
    public FakeP2PNetwork Network { get; }
    public FakeP2PCloud Cloud { get; } = new();
    public EufyP2PClient Client { get; }

    /// <summary>A copy of <paramref name="s"/> with the given timings replaced.</summary>
    public static EufyP2PClientSettings With(EufyP2PClientSettings s, TimeSpan? connectTimeout = null, TimeSpan? level2Grace = null, TimeSpan? level2Settle = null,
        TimeSpan? resultGrace = null, TimeSpan? snapshotTimeout = null, Func<byte[], CancellationToken, Task<byte[]>>? decoder = null, TimeSpan? batteryIdle = null) => new()
    {
        Transport = s.Transport,
        ConnectTimeout = connectTimeout ?? s.ConnectTimeout,
        Level2Grace = level2Grace ?? s.Level2Grace,
        Level2Settle = level2Settle ?? s.Level2Settle,
        DirectCommandSends = s.DirectCommandSends,
        DirectCommandInterval = s.DirectCommandInterval,
        ResultGrace = resultGrace ?? s.ResultGrace,
        SnapshotTimeout = snapshotTimeout ?? s.SnapshotTimeout,
        SnapshotFallbackAfter = s.SnapshotFallbackAfter,
        BatteryIdle = batteryIdle ?? s.BatteryIdle,
        NoBroadcast = s.NoBroadcast,
        SnapshotDecoder = decoder ?? s.SnapshotDecoder,
    };

    public ValueTask DisposeAsync() => Client.DisposeAsync();
}

/// <summary>
/// <see cref="EufyP2PClient.SetGuardModeAsync"/> end to end against a fake station: the exact cmd 1224 frames the
/// station receives at level 1 and level 2, the T8410C direct frame, the result handling and the refusals.
/// </summary>
[TestClass]
public class EufyP2PClientGuardModeTests
{
    private const string Sn = "T8030P0000000001";

    private static EufyDeviceRecord Station(string model = "T8030", int? deviceType = 18, string sn = Sn) => P2PDevices.Record(sn, model, did: P2PRig.Did, deviceType: deviceType);

    private static string Envelope(int wire) =>
        $"{{\"account_id\":\"admin-0001\",\"cmd\":1224,\"mChannel\":0,\"mValue3\":0,\"payload\":{{\"mode_type\":{wire},\"user_name\":\"someone+tag\"}}}}";

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(63)]
    public async Task SendsTheCapturedEnvelopeAtLevel1ToAStationWithoutAKey(int wire)
    {
        await using var rig = new P2PRig(Sn, [Station()]);
        await rig.Client.SetGuardModeAsync(Sn, wire, CancellationToken.None).Bounded();

        var frames = rig.Station.CommandsOf(1350);
        Assert.HasCount(5, frames);
        var body = TestCrypto.EcbEncrypt(rig.Station.Level1Key, TestCrypto.ZeroPad(Encoding.UTF8.GetBytes(Envelope(wire))));
        for (var i = 0; i < frames.Count; i++)
        {
            var seq = i + 1; // seq 0 is the gateway prompt sent on connect
            var lengthLe = new byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(lengthLe, (ushort)body.Length);
            byte[] data = [0xd1, 0x00, (byte)(seq >> 8), (byte)seq, .. "XZYH"u8, 0x46, 0x05, .. lengthLe, 0x00, 0x00, 0x01, 0x00, 0x00, 0x01, 0x00, 0x00, .. body];
            CollectionAssert.AreEqual(P2PCodec.FrameMessage(0xf1d0, data), frames[i].Datagram, $"datagram {i}");
            Assert.AreEqual(Envelope(wire), frames[i].Text);
        }
        Assert.AreEqual(1, rig.Station.Ends, "the session is reset after a guard-mode write");
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(63)]
    public async Task SealsTheEnvelopeAtLevel2OnAKeyedStationAndReadsItsResult(int wire)
    {
        using var cipher = TestCrypto.NewP256();
        await using var rig = new P2PRig(Sn, [Station()], s =>
        {
            s.Cipher = cipher;
            s.ResultFor = c => c.Cmd == 1350 ? 0 : null;
        }, s => P2PRig.With(s, level2Settle: TimeSpan.FromSeconds(10), resultGrace: TimeSpan.FromSeconds(10)));
        await rig.Client.SetGuardModeAsync(Sn, wire, CancellationToken.None).Bounded();

        var frames = rig.Station.CommandsOf(1350);
        Assert.HasCount(5, frames);
        for (var i = 0; i < frames.Count; i++)
        {
            var f = frames[i];
            Assert.AreEqual(8, f.SignCode);
            Assert.AreEqual((byte)0x08, f.Magic0);
            Assert.AreEqual((byte)0x00, f.Magic1);
            Assert.AreEqual(0, f.Channel);
            Assert.AreEqual(0, f.StreamId);
            Assert.AreEqual(f.Body.Length, BinaryPrimitives.ReadUInt16LittleEndian(f.Datagram.AsSpan(14)));
            Assert.AreEqual($"{i:X2}030201", Convert.ToHexString(f.Body, 28, 4));
            Assert.AreEqual(Envelope(wire), f.Text);
        }
        var request = rig.Cloud.CipherRequests.Single();
        Assert.AreEqual(7, request.GetProperty("cipher_ids")[0].GetInt32());
        Assert.AreEqual("admin-0001", request.GetProperty("user_id").GetString());
        Assert.AreEqual(Sn, request.GetProperty("station_sn").GetString());
    }

    [TestMethod]
    public async Task AStationRefusalIsReportedWithItsCode()
    {
        using var cipher = TestCrypto.NewP256();
        await using var rig = new P2PRig(Sn, [Station()], s =>
        {
            s.Cipher = cipher;
            s.ResultFor = c => c.Cmd == 1350 ? -103 : null;
        }, s => P2PRig.With(s, level2Settle: TimeSpan.FromSeconds(10), resultGrace: TimeSpan.FromSeconds(10)));
        var error = await Assert.ThrowsExactlyAsync<EufyP2PCommandException>(() => rig.Client.SetGuardModeAsync(Sn, 1, CancellationToken.None).Bounded());
        Assert.AreEqual(-103, error.Code);
        StringAssert.Contains(error.Message, "guard mode home (1)");
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(63)]
    public async Task SendsTheDirectFrameToAT8410C(int wire)
    {
        using var cipher = TestCrypto.NewP256();
        await using var rig = new P2PRig(Sn, [Station("T8410C", 31)], s =>
        {
            s.Cipher = cipher;
            s.ResultFor = c => c.Cmd == 1224 ? 0 : null;
        }, s => P2PRig.With(s, level2Grace: TimeSpan.FromSeconds(10), resultGrace: TimeSpan.FromSeconds(10)));
        await rig.Client.SetGuardModeAsync(Sn, wire, CancellationToken.None).Bounded();

        Assert.IsEmpty(rig.Station.CommandsOf(1350));
        var frames = rig.Station.CommandsOf(1224);
        Assert.HasCount(5, frames);
        byte[] expected = [(byte)wire, 0, 0, 0, .. "admin-0001"u8, .. new byte[118]];
        foreach (var f in frames)
        {
            Assert.AreEqual(8, f.SignCode);
            Assert.AreEqual((byte)0x08, f.Magic0);
            Assert.AreEqual(0, f.Channel);
            CollectionAssert.AreEqual(expected, f.Plain);
        }
    }

    [TestMethod]
    public async Task AT8410CWithoutALevel2KeyIsRefusedDescriptively()
    {
        await using var rig = new P2PRig(Sn, [Station("T8410C", 31)]);
        var error = await Assert.ThrowsExactlyAsync<EufyStationKeyUnavailableException>(() => rig.Client.SetGuardModeAsync(Sn, 0, CancellationToken.None).Bounded());
        Assert.AreEqual(Sn, error.StationSerial);
        Assert.IsEmpty(rig.Station.CommandsOf(1224));
    }

    [TestMethod]
    public async Task AnUnreachableStationFailsWithTheConnectTimeout()
    {
        await using var rig = new P2PRig(Sn, [Station()], s => s.Reachable = false, s => P2PRig.With(s, connectTimeout: TimeSpan.FromMilliseconds(300)));
        var error = await Assert.ThrowsExactlyAsync<EufyStationUnreachableException>(() => rig.Client.SetGuardModeAsync(Sn, 0, CancellationToken.None).Bounded());
        StringAssert.Contains(error.Message, "P2P connect timeout for station " + Sn);

        // The next call opens a fresh session rather than reusing the dead one.
        rig.Station.Reachable = true;
        await rig.Client.SetGuardModeAsync(Sn, 0, CancellationToken.None).Bounded();
        Assert.HasCount(5, rig.Station.CommandsOf(1350));
    }

    [TestMethod]
    public async Task ConcurrentWritesToOneStationAreSerialized()
    {
        await using var rig = new P2PRig(Sn, [Station()]);
        await Task.WhenAll(
            rig.Client.SetGuardModeAsync(Sn, 0, CancellationToken.None),
            rig.Client.SetGuardModeAsync(Sn, 1, CancellationToken.None)).Bounded();

        var frames = rig.Station.CommandsOf(1350);
        Assert.HasCount(10, frames);
        var first = frames[0].Text;
        Assert.IsTrue(frames.Take(5).All(f => f.Text == first) && frames.Skip(5).All(f => f.Text != first), "one write's frames never interleave with the other's");
        Assert.IsTrue(frames.Take(5).All(f => f.From == frames[0].From) && frames[5].From != frames[0].From, "each write had its own session");
        Assert.IsTrue(frames[0].From.Disposed);
    }

    [TestMethod]
    [DataRow(7)]
    [DataRow(-1)]
    [DataRow(64)]
    public async Task RefusesAnInvalidModeBeforeTouchingTheNetwork(int wire)
    {
        await using var rig = new P2PRig(Sn, [Station()]);
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => rig.Client.SetGuardModeAsync(Sn, wire, CancellationToken.None));
        Assert.IsEmpty(rig.Network.Sockets);
    }

    [TestMethod]
    [DataRow("T8030", 27)]
    [DataRow("T8N00", 50)]
    [DataRow("T8223", 91)]
    public async Task RefusesStationsEufyDrivesOverWebRtc(string model, int deviceType)
    {
        await using var rig = new P2PRig(Sn, [Station(model, deviceType)]);
        var error = await Assert.ThrowsExactlyAsync<NotSupportedException>(() => rig.Client.SetGuardModeAsync(Sn, 0, CancellationToken.None));
        StringAssert.Contains(error.Message, "WebRTC");
        Assert.IsEmpty(rig.Network.Sockets);
    }

    [TestMethod]
    public async Task RefusesWhatItCannotAddress()
    {
        await using var rig = new P2PRig(Sn, [Station(), P2PDevices.Record("T8410P0000000009", "T8410C", deviceType: 31, did: P2PRig.Did)]);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => rig.Client.SetGuardModeAsync("T8030P0000000404", 0, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => rig.Client.SetGuardModeAsync("T8410P0000000009", 2, CancellationToken.None));
        rig.Cloud.AccountName = "";
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => rig.Client.SetGuardModeAsync(Sn, 0, CancellationToken.None));
        StringAssert.Contains(error.Message, "missing account identity");
        Assert.IsEmpty(rig.Network.Sockets);

        await rig.Client.DisposeAsync();
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => rig.Client.SetGuardModeAsync(Sn, 0, CancellationToken.None));
    }
}
