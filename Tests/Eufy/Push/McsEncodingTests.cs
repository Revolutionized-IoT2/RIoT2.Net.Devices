using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RIoT2.Net.Devices.Services.Eufy.Push;

namespace RIoT2.Net.Devices.Tests.Eufy.Push;

/// <summary>
/// Wire encodings, pinned to bytes produced by protobufjs from upstream's own schemas (proto.ts) with the
/// objects upstream builds (push-client.ts, fcm.ts).
/// </summary>
[TestClass]
public class McsEncodingTests
{
    private const string AndroidId = "5123456789012345678";
    private const string SecurityToken = "1234567890123456789";

    private static byte[] Hex(string hex) => Convert.FromHexString(hex);

    private static McsPushClient Client(params string[] persistentIds)
    {
        var creds = new FcmCredentials { Fid = "f", AndroidId = AndroidId, SecurityToken = SecurityToken, FcmToken = "t" };
        var client = new McsPushClient(creds, _ => throw new NotSupportedException(), new NoHandler(), NullLogger.Instance);
        client.SetPersistentIds(persistentIds);
        return client;
    }

    [TestMethod]
    public void LoginRequestMatchesUpstreamBytes()
    {
        const string expected =
            "29029f010a126368726f6d652d36332e302e333233342e30120f6d63732e616e64726f69642e636f6d1a1335313233343536373839303132333435363738"
            + "2213353132333435363738393031323334353637382a13313233343536373839303132333435363738393218616e64726f69642d34373161326363646562"
            + "323466333465420b0a066e65775f76631201315203303a315203303a3260007001800102880101";

        CollectionAssert.AreEqual(Hex(expected), Client("0:1", "0:2").BuildLoginRequest());
    }

    [TestMethod]
    public void LoginRequestCarriesTheCredentialsAndPersistentIds()
    {
        var frame = Client("0:1", "0:2").BuildLoginRequest();
        Assert.AreEqual(41, frame[0]);
        Assert.AreEqual((byte)McsMessageTag.LoginRequest, frame[1]);
        var messages = new McsParser().HandleData(frame);
        var login = (McsLoginRequest)messages.Single().Message!;

        Assert.AreEqual("chrome-63.0.3234.0", login.Id);
        Assert.AreEqual("mcs.android.com", login.Domain);
        Assert.AreEqual(AndroidId, login.User);
        Assert.AreEqual(AndroidId, login.Resource);
        Assert.AreEqual(SecurityToken, login.AuthToken);
        Assert.AreEqual("android-471a2ccdeb24f34e", login.DeviceId);
        Assert.AreEqual(2, login.AuthService);
        Assert.AreEqual(1, login.NetworkType);
        Assert.IsTrue(login.UseRmq2);
        Assert.IsFalse(login.AdaptiveHeartbeat);
        Assert.AreEqual(new McsSetting("new_vc", "1"), login.Setting.Single());
        CollectionAssert.AreEqual(new[] { "0:1", "0:2" }, login.ReceivedPersistentId);
    }

    [TestMethod]
    public void HeartbeatPingAndAckMatchUpstreamBytes()
    {
        CollectionAssert.AreEqual(Hex("0000"), McsPushClient.BuildHeartbeatPing());
        CollectionAssert.AreEqual(Hex("01021007"), McsPushClient.BuildHeartbeatAck(7));
        // A falsy lastStreamIdReceived is left out, as upstream's `lastStreamId ? {...} : {}`.
        CollectionAssert.AreEqual(Hex("0100"), McsPushClient.BuildHeartbeatAck(0));
        CollectionAssert.AreEqual(Hex("0100"), McsPushClient.BuildHeartbeatAck(null));
    }

    [TestMethod]
    public void CheckinRequestMatchesUpstreamBytes()
    {
        const string expected =
            "100022650a610a37676f6f676c652f72617a6f722f666c6f3a352e302e312f4c52583232432f313630323135383a757365722f72656c656173652d6b6579"
            + "731203666c6f1a06676f6f676c652209464c4f2d30342e3034320e616e64726f69642d676f6f676c6510003202656e38d285d8cc046203474d547003a00100b00100";

        CollectionAssert.AreEqual(Hex(expected), FcmRegistrar.BuildCheckinRequest());
    }

    [TestMethod]
    public void CheckinResponseDecodesTheFixed64Credentials()
    {
        var response = CheckinResponse.Decode(Hex("08011880d095ffbc31394ef324ebcd2c1a474102000000000000f05a0178"));

        Assert.IsTrue(response.StatsOk);
        Assert.AreEqual(1700000000000L, response.TimeMs);
        Assert.AreEqual(5123456789012345678UL, response.AndroidId);
        Assert.AreEqual(17293822569102704642UL, response.SecurityToken);
    }

    [TestMethod]
    public void LoginResponseDecodesUpstreamBytes()
    {
        var ok = McsLoginResponse.Decode(Hex("0a126368726f6d652d36332e302e333233342e301203784079280130014080d095ffbc31"));
        Assert.AreEqual("chrome-63.0.3234.0", ok.Id);
        Assert.AreEqual("x@y", ok.Jid);
        Assert.AreEqual(1, ok.StreamId);
        Assert.AreEqual(1, ok.LastStreamIdReceived);
        Assert.AreEqual(1700000000000L, ok.ServerTimestamp);
        Assert.IsNull(ok.Error);

        var rejected = McsLoginResponse.Decode(Hex("0a01781a11089103120c77726f6e675f736563726574"));
        Assert.AreEqual(401, rejected.Error!.Code);
        Assert.AreEqual("wrong_secret", rejected.Error.Message);
    }

    [TestMethod]
    public void DataMessageStanzaDecodesUpstreamBytes()
    {
        var stanza = McsDataMessageStanza.Decode(Hex(PushNormalizerTests.GoldenDataMessage));

        Assert.AreEqual("m1", stanza.Id);
        Assert.AreEqual("348804314802", stanza.From);
        Assert.AreEqual("com.oceanwing.battery.cam", stanza.Category);
        Assert.AreEqual("0:1700000000000%abc", stanza.PersistentId);
        Assert.AreEqual(2419200, stanza.Ttl);
        Assert.AreEqual(1700000000000L, stanza.Sent);
        Assert.HasCount(2, stanza.AppData);
        Assert.AreEqual(new McsAppData("station_sn", "T8000P0000000000"), stanza.AppData[0]);
        Assert.AreEqual("payload", stanza.AppData[1].Key);
        // Round trip.
        CollectionAssert.AreEqual(Hex(PushNormalizerTests.GoldenDataMessage), stanza.Encode());
    }

    [TestMethod]
    public void NegativeInt32IsSignExtendedAndReadBack()
    {
        var bytes = new McsHeartbeat { StreamId = -1 }.Encode();
        Assert.HasCount(11, bytes);
        Assert.AreEqual(-1, McsHeartbeat.Decode(bytes).StreamId);
    }

    [TestMethod]
    public void TruncatedProtobufIsRejected()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => McsLoginResponse.Decode(Hex("0a12636872")));
    }

    [TestMethod]
    public void ReconnectBackoffFollowsUpstream()
    {
        var client = Client();
        var delays = Enumerable.Range(0, 18).Select(_ => (int)client.NextReconnectDelay().TotalSeconds).ToArray();
        CollectionAssert.AreEqual(
            new[] { 5, 10, 20, 30, 40, 50, 60, 120, 180, 240, 300, 360, 420, 480, 540, 600, 600, 600 },
            delays);
    }

    private sealed class NoHandler : IMcsPushHandler
    {
        public Task OnConnectedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void OnDisconnected() { }
        public Task OnPushAsync(PushEvent push, CancellationToken cancellationToken) => Task.CompletedTask;
        public void OnError(Exception error) { }
    }
}
