using System.Buffers.Binary;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RIoT2.Net.Devices.Services.Eufy.P2P;

namespace RIoT2.Net.Devices.Tests.Eufy.P2P;

/// <summary>
/// The PPCS session against an in-memory station: LAN lookup → CHECK_CAM → CAM_ID, the gateway prompt and level-2
/// negotiation, ACKs, sequencing and reassembly, the connect deadline and END on close. The command-result vectors
/// are eufy-sdk's <c>command-result.spec.ts</c>.
/// </summary>
[TestClass]
public class P2PSessionTests
{
    private const string Sn = "T8010P0000000001";
    private const string Did = "EUPRCAM-000000-XXXXX";

    private static (P2PSession Session, FakeStation Station, FakeP2PNetwork Network) Connectable(Action<FakeStation>? setup = null, Func<int, CancellationToken, Task<P2PCipherKeys?>>? resolve = null, TimeSpan? connectTimeout = null)
    {
        var station = new FakeStation(Sn, Did);
        setup?.Invoke(station);
        var network = new FakeP2PNetwork { Station = station };
        var session = new P2PSession(new P2PSessionConfig
        {
            StationSn = Sn,
            P2pDid = Did,
            Transport = network,
            ResolveCipherKey = resolve,
            ConnectTimeout = connectTimeout,
        });
        return (session, station, network);
    }

    private static List<P2PCommandResult> Results(int commandId, byte[] body, int signCode = 0)
    {
        var session = new P2PSession(new P2PSessionConfig { StationSn = "T8000P0000000000", P2pDid = "XXXXXXX-000000-XXXXX", Transport = new FakeP2PNetwork() });
        var seen = new List<P2PCommandResult>();
        session.CommandResult += seen.Add;
        session.HandleFrame(new P2PDataFrameHeader(commandId, (uint)body.Length, 0, signCode, 0), body, 0);
        return seen;
    }

    private static byte[] Padded(int value, int length = 132, byte[]? tail = null)
    {
        var b = new byte[length];
        BinaryPrimitives.WriteInt32LittleEndian(b, value);
        tail?.AsSpan(0, Math.Min(tail.Length, length - 4)).CopyTo(b.AsSpan(4));
        return b;
    }

    [TestMethod]
    public void ReadsABareInt32AsTheCommandResultWhateverTheCommand()
    {
        CollectionAssert.AreEqual(new[] { new P2PCommandResult(1350, 0, 0) }, Results(1350, TestCrypto.Int32Le(0)));
        CollectionAssert.AreEqual(new[] { new P2PCommandResult(1700, -108, 0) }, Results(1700, TestCrypto.Int32Le(-108)));
        CollectionAssert.AreEqual(new[] { new P2PCommandResult(1350, -104, 0) }, Results(1350, TestCrypto.Int32Le(-104)));
        CollectionAssert.AreEqual(new[] { new P2PCommandResult(1224, 0, 0) }, Results(1224, TestCrypto.Int32Le(0)));
    }

    [TestMethod]
    public void ReportsNoResultForMediaJsonShortLongOrTextBodies()
    {
        Assert.IsEmpty(Results(1300, TestCrypto.Int32Le(0)));
        Assert.IsEmpty(Results(1301, TestCrypto.Int32Le(0)));
        Assert.IsEmpty(Results(1700, [.. "{\"cmd\":1306,\"count\":0}"u8, 0]));
        Assert.IsEmpty(Results(1700, [0, 1, 2]));
        Assert.IsEmpty(Results(1700, Enumerable.Repeat((byte)0xa5, 16).ToArray()));
        Assert.IsEmpty(Results(1700, "not a result"u8.ToArray()));
    }

    [TestMethod]
    public void ReadsThePadded132ByteSetPayloadAnswerOnlyInTheClear()
    {
        var staleTail = Encoding.Latin1.GetBytes("devs_list HTTP/1.1\r\nHost: example.invalid\r\nX-Random-Number: 4096\r\n");
        CollectionAssert.AreEqual(new[] { new P2PCommandResult(1350, -110, 0) }, Results(1350, Padded(-110)));
        CollectionAssert.AreEqual(new[] { new P2PCommandResult(1350, 0, 0) }, Results(1350, Padded(0)));
        CollectionAssert.AreEqual(new[] { new P2PCommandResult(1350, -151, 0) }, Results(1350, Padded(-151, 132, staleTail)));
        Assert.IsEmpty(Results(1350, Padded(-110), 1));
        Assert.IsEmpty(Results(1350, Padded(-110), 8));
        Assert.IsEmpty(Results(1700, Padded(-110)));
        Assert.IsEmpty(Results(1350, Padded(-110, 64)));
        Assert.IsEmpty(Results(1350, Padded(-110, 148)));
    }

    [TestMethod]
    public async Task ConnectsOverTheLanLookupAndPromptsTheGateway()
    {
        var (session, station, network) = Connectable();
        var connected = 0;
        session.Connected += () => connected++;
        await session.ConnectAsync().Bounded();
        await session.WaitConnectedAsync(CancellationToken.None).Bounded();
        try
        {
            Assert.IsTrue(session.IsConnected);
            Assert.AreEqual(1, connected);
            Assert.IsTrue(network.Sockets[0].Broadcast);
            await AsyncTestExtensions.Until(() => station.CommandsOf(1100).Count > 0 && station.Pings > 0, "the gateway prompt and the first ping");
            var gateway = station.CommandsOf(1100)[0];
            Assert.AreEqual(0, gateway.Seq);
            Assert.AreEqual(255, gateway.Channel);
            Assert.AreEqual("D1000000" + "585A5948" + "4C04" + "00000000" + "0100FF00" + "0000", Convert.ToHexString(gateway.Datagram, 4, gateway.Datagram.Length - 4));
        }
        finally
        {
            await session.CloseAsync().Bounded();
        }
        Assert.AreEqual(1, station.Ends);
        Assert.IsTrue(network.Sockets.All(s => s.Disposed));
        Assert.IsTrue(session.IsClosed);
    }

    [TestMethod]
    public async Task NegotiatesTheLevel2KeyFromTheGatewayInfo()
    {
        using var cipher = TestCrypto.NewP256();
        var asked = new List<int>();
        var (session, station, _) = Connectable(s => s.Cipher = cipher, (id, _) =>
        {
            lock (asked) asked.Add(id);
            return Task.FromResult<P2PCipherKeys?>(new P2PCipherKeys(TestCrypto.PrivateKeyHex(cipher), null));
        });
        var ready = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Level2Ready += id => ready.TrySetResult(id);
        await session.ConnectAsync().Bounded();
        try
        {
            Assert.IsTrue(await session.AwaitLevel2KeyAsync(TimeSpan.FromSeconds(10)).Bounded());
            Assert.AreEqual(7, await ready.Task.Bounded());
            CollectionAssert.AreEqual(new[] { 7 }, asked);
            Assert.IsTrue(session.HasLevel2Key);

            // A level-2 frame from the station now opens; the station opens ours.
            Assert.IsTrue(session.SendRawLevel2Bytes([1, 2, 3, 4], 0, 1035));
            await AsyncTestExtensions.Until(() => station.CommandsOf(1035).Count == 1, "the level-2 frame");
            var frame = station.CommandsOf(1035)[0];
            Assert.AreEqual(8, frame.SignCode);
            Assert.AreEqual(0x08, frame.Magic0);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, frame.Plain);
        }
        finally
        {
            await session.CloseAsync().Bounded();
        }
    }

    [TestMethod]
    public async Task ASessionWithoutACipherSettlesWithoutAKey()
    {
        var (session, _, _) = Connectable(resolve: (_, _) => Task.FromResult<P2PCipherKeys?>(null));
        await session.ConnectAsync().Bounded();
        await session.WaitConnectedAsync(CancellationToken.None).Bounded();
        try
        {
            Assert.IsFalse(await session.AwaitLevel2KeyAsync(TimeSpan.FromMilliseconds(200)).Bounded());
            Assert.IsFalse(session.HasLevel2Key);
        }
        finally
        {
            await session.CloseAsync().Bounded();
        }
        Assert.IsFalse(await new P2PSession(new P2PSessionConfig { StationSn = Sn, P2pDid = Did, Transport = new FakeP2PNetwork() }).AwaitLevel2KeyAsync(TimeSpan.FromSeconds(5)).Bounded());
    }

    [TestMethod]
    public async Task SendsALevel1SetPayloadTheStationOpens()
    {
        var (session, station, _) = Connectable();
        await session.ConnectAsync().Bounded();
        await session.WaitConnectedAsync(CancellationToken.None).Bounded();
        try
        {
            session.SendSetPayload(1224, new System.Text.Json.Nodes.JsonObject { ["mode_type"] = 1 }, "acct", 0);
            await AsyncTestExtensions.Until(() => station.CommandsOf(1350).Count == 1, "the set-payload");
            var frame = station.CommandsOf(1350)[0];
            Assert.AreEqual(1, frame.SignCode);
            Assert.AreEqual(1, frame.Seq);
            Assert.AreEqual("{\"account_id\":\"acct\",\"cmd\":1224,\"mChannel\":0,\"mValue3\":0,\"payload\":{\"mode_type\":1}}", frame.Text);
        }
        finally
        {
            await session.CloseAsync().Bounded();
        }
        Assert.ThrowsExactly<InvalidOperationException>(() => session.SendSetPayload(1224, null, "acct", 0));
    }

    [TestMethod]
    public async Task AcknowledgesAndReordersInboundDataAndReassemblesSplitFrames()
    {
        var (session, station, network) = Connectable();
        var frames = new List<P2PFrame>();
        session.FrameReceived += f => { lock (frames) frames.Add(f); };
        await session.ConnectAsync().Bounded();
        await session.WaitConnectedAsync(CancellationToken.None).Bounded();
        try
        {
            var socket = network.Sockets[0];
            byte[] Json(int n) => Encoding.UTF8.GetBytes($"{{\"n\":{n}}}\0");
            byte[] Datagram(int seq, byte[] json) => P2PCodec.FrameMessage(P2PMessageType.Data, FakeStation.DataDatagramBody(seq, 1351, 0, 0, json));

            socket.Push(Datagram(10, Json(1)), station.Address);
            socket.Push(Datagram(12, Json(3)), station.Address);
            socket.Push(Datagram(11, Json(2)), station.Address);
            socket.Push(Datagram(11, Json(2)), station.Address);

            // One frame split over two datagrams: the header names more bytes than the first carries.
            var whole = FakeStation.DataDatagramBody(13, 1351, 0, 0, Json(4));
            socket.Push(P2PCodec.FrameMessage(P2PMessageType.Data, whole.AsSpan(0, 14)), station.Address);
            byte[] rest = [0xd1, 0x00, 0x00, 14, .. whole[14..]];
            socket.Push(P2PCodec.FrameMessage(P2PMessageType.Data, rest), station.Address);

            await AsyncTestExtensions.Until(() => { lock (frames) return frames.Count >= 4; }, "four frames");
            lock (frames)
            {
                CollectionAssert.AreEqual(new[] { 1, 2, 3, 4 }, frames.Select(f => f.Json!.Value.GetProperty("n").GetInt32()).ToArray());
                Assert.IsTrue(frames.All(f => f.CommandId == 1351));
            }
        }
        finally
        {
            await session.CloseAsync().Bounded();
        }
    }

    [TestMethod]
    public async Task GivesUpWithADescriptiveErrorWhenTheStationNeverAnswers()
    {
        var (session, station, _) = Connectable(s => s.Reachable = false, connectTimeout: TimeSpan.FromMilliseconds(300));
        Exception? reported = null;
        session.Error += e => reported = e;
        await session.ConnectAsync().Bounded();
        var error = await Assert.ThrowsExactlyAsync<EufyStationUnreachableException>(() => session.WaitConnectedAsync(CancellationToken.None).Bounded());
        StringAssert.Contains(error.Message, "P2P connect timeout for station " + Sn);
        Assert.AreEqual(Sn, error.StationSerial);
        Assert.IsInstanceOfType<EufyStationUnreachableException>(reported);
        Assert.IsTrue(session.IsClosed);
        Assert.AreEqual(0, station.Ends);
    }

    [TestMethod]
    public async Task ClosesWhenTheStationEndsTheSession()
    {
        var (session, station, network) = Connectable();
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Closed += () => closed.TrySetResult();
        await session.ConnectAsync().Bounded();
        await session.WaitConnectedAsync(CancellationToken.None).Bounded();
        network.Sockets[0].Push(P2PCodec.FrameMessage(P2PMessageType.End), station.Address);
        await closed.Task.Bounded();
        Assert.IsTrue(session.IsClosed);
        Assert.IsFalse(session.IsConnected);
    }
}
