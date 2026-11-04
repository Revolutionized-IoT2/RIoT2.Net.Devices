using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RIoT2.Net.Devices.Services.Eufy;
using RIoT2.Net.Devices.Services.Eufy.Push;

namespace RIoT2.Net.Devices.Tests.Eufy.Push;

/// <summary>An MCS session over an in-memory stream: login, events, persistent ids, reconnects.</summary>
[TestClass]
public class EufyPushClientTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private string _dir = null!;

    [TestInitialize]
    public void CreateDirectory()
    {
        _dir = Path.Combine(AppContext.BaseDirectory, "TestData", "eufy-push-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void DeleteDirectory() => Directory.Delete(_dir, true);

    private EufyAccountOptions Options => new() { Email = "user@example.com", Password = "secret", DataDirectory = _dir };

    private string StorePath => Path.Combine(_dir, FileFcmStore.FileNameFor("user@example.com"));

    private async Task SeedStoreAsync(params string[] persistentIds) =>
        await new FileFcmStore(StorePath).SaveAsync(new PersistedPush { Creds = FcmStoreTests.Creds(), PersistentIds = persistentIds }, CancellationToken.None);

    private static byte[] LoginResponse(bool first = true, McsErrorInfo? error = null)
    {
        var body = new McsLoginResponse { Id = "chrome-63.0.3234.0", StreamId = 1, Error = error }.Encode();
        return McsPushClient.Frame(first ? [41, (byte)McsMessageTag.LoginResponse] : [(byte)McsMessageTag.LoginResponse], body);
    }

    private static byte[] DataMessage(string persistentId, string payloadJson, params (string Key, string Value)[] extra)
    {
        var stanza = new McsDataMessageStanza { From = "348804314802", Category = "com.oceanwing.battery.cam", PersistentId = persistentId };
        foreach (var (key, value) in extra) stanza.AppData.Add(new McsAppData(key, value));
        stanza.AppData.Add(new McsAppData("payload", PushNormalizerTests.Encoded(payloadJson)));
        return McsPushClient.Frame([(byte)McsMessageTag.DataMessageStanza], stanza.Encode());
    }

    private static async Task<T> WithTimeout<T>(Task<T> task) => await task.WaitAsync(Timeout);

    private static async Task WithTimeout(Task task) => await task.WaitAsync(Timeout);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("condition not met in time");
            await Task.Delay(20);
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("condition not met in time");
            await Task.Delay(20);
        }
    }

    /// <summary>Null when the file is (momentarily) unreadable.</summary>
    private static async Task<IReadOnlyList<string>?> PersistedIdsAsync(string path) =>
        (await new FileFcmStore(path).LoadAsync(CancellationToken.None))?.PersistentIds;

    [TestMethod]
    public async Task LogsInRaisesEventsAndSavesPersistentIds()
    {
        await SeedStoreAsync("0:old");
        var server = new FakeMcsServer();
        var cloud = new FakePushCloud();
        var events = new List<EufySecurityEvent>();
        var connection = new List<bool>();
        await using var client = new EufyPushClient(Options, cloud, NullLogger.Instance, new EufyPushClientHooks { Connect = server.ConnectAsync, Delay = (_, ct) => Task.Delay(1, ct) });
        client.EventReceived += e => { lock (events) events.Add(e); };
        client.ConnectionChanged += c => { lock (connection) connection.Add(c); };

        var start = client.StartAsync(CancellationToken.None);
        var stream = await WithTimeout(server.Connections.Reader.ReadAsync().AsTask());
        var login = (McsLoginRequest)(await WithTimeout(stream.ReadClientMessageAsync())).Message!;
        Assert.AreEqual("5123456789012345678", login.User);
        Assert.AreEqual("1234567890123456789", login.AuthToken);
        CollectionAssert.AreEqual(new[] { "0:old" }, login.ReceivedPersistentId);
        Assert.IsFalse(start.IsCompleted);

        stream.ServerSend(LoginResponse());
        await WithTimeout(start);
        Assert.IsTrue(client.IsConnected);
        lock (connection) CollectionAssert.AreEqual(new[] { true }, connection);
        CollectionAssert.AreEqual(new[] { "fcm-token:abc" }, cloud.RegisteredTokens);
        // The login acknowledged the reported id.
        await WaitUntilAsync(async () => await PersistedIdsAsync(StorePath) is { Count: 0 });

        stream.ServerSend(DataMessage("0:p1", "{\"event_type\":3102,\"pic_url\":\"https://example.test/t.jpg\",\"objects\":{\"names\":[\"Alice\"]}}",
            ("device_sn", "T8000P0000000001"), ("station_sn", "T8000P0000000000")));
        await WaitUntilAsync(() => { lock (events) return events.Count == 1; });
        var e = events[0];
        Assert.AreEqual(EufyEventKind.PersonDetected, e.Kind);
        Assert.AreEqual("T8000P0000000001", e.DeviceSerial);
        Assert.AreEqual("T8000P0000000000", e.StationSerial);
        Assert.AreEqual("https://example.test/t.jpg", e.ThumbnailUrl);
        Assert.AreEqual("Alice", e.PersonName);
        Assert.AreEqual(3102, e.EventType);
        await WaitUntilAsync(async () => (await PersistedIdsAsync(StorePath))?.SequenceEqual(["0:p1"]) == true);

        // The server's heartbeat ping is acknowledged.
        stream.ServerSend(McsPushClient.Frame([(byte)McsMessageTag.HeartbeatPing], new McsHeartbeat { LastStreamIdReceived = 5 }.Encode()));
        var ack = await WithTimeout(stream.ReadClientMessageAsync());
        Assert.AreEqual((int)McsMessageTag.HeartbeatAck, ack.Tag);
        Assert.AreEqual(5, ((McsHeartbeat)ack.Message!).LastStreamIdReceived);

        await client.StopAsync();
        Assert.IsFalse(client.IsConnected);
        lock (connection) CollectionAssert.AreEqual(new[] { true, false }, connection);
    }

    [TestMethod]
    public async Task ASubscriberExceptionDoesNotStopTheReader()
    {
        await SeedStoreAsync();
        var server = new FakeMcsServer();
        var received = new List<EufyEventKind>();
        await using var client = new EufyPushClient(Options, new FakePushCloud(), NullLogger.Instance, new EufyPushClientHooks { Connect = server.ConnectAsync });
        client.EventReceived += _ => throw new InvalidOperationException("subscriber bug");
        client.EventReceived += e => { lock (received) received.Add(e.Kind); };

        var start = client.StartAsync(CancellationToken.None);
        var stream = await WithTimeout(server.Connections.Reader.ReadAsync().AsTask());
        stream.ServerSend(LoginResponse());
        await WithTimeout(start);

        stream.ServerSend(DataMessage("0:1", "{\"event_type\":3103}"));
        stream.ServerSend(DataMessage("0:2", "{\"a\":10,\"type\":16}"));
        await WaitUntilAsync(() => { lock (received) return received.Count == 2; });
        CollectionAssert.AreEqual(new[] { EufyEventKind.DoorbellPress, EufyEventKind.Alarm }, received);
        Assert.IsTrue(client.IsConnected);
    }

    [TestMethod]
    public async Task ReconnectsAfterADropAndReportsTheUnacknowledgedIds()
    {
        await SeedStoreAsync();
        var server = new FakeMcsServer();
        var delays = new List<TimeSpan>();
        var connection = new List<bool>();
        await using var client = new EufyPushClient(Options, new FakePushCloud(), NullLogger.Instance, new EufyPushClientHooks
        {
            Connect = server.ConnectAsync,
            Delay = (d, ct) => { lock (delays) delays.Add(d); return Task.Delay(1, ct); },
        });
        client.ConnectionChanged += c => { lock (connection) connection.Add(c); };

        var start = client.StartAsync(CancellationToken.None);
        var first = await WithTimeout(server.Connections.Reader.ReadAsync().AsTask());
        await WithTimeout(first.ReadClientMessageAsync());
        first.ServerSend(LoginResponse());
        await WithTimeout(start);
        first.ServerSend(DataMessage("0:a", "{\"event_type\":3101}"));
        await WaitUntilAsync(async () => (await PersistedIdsAsync(StorePath))?.Contains("0:a") == true);

        first.ServerClose();
        var second = await WithTimeout(server.Connections.Reader.ReadAsync().AsTask());
        var login = (McsLoginRequest)(await WithTimeout(second.ReadClientMessageAsync())).Message!;
        CollectionAssert.AreEqual(new[] { "0:a" }, login.ReceivedPersistentId);
        second.ServerSend(LoginResponse());
        await WaitUntilAsync(() => client.IsConnected);
        await WaitUntilAsync(async () => await PersistedIdsAsync(StorePath) is { Count: 0 });

        lock (delays) CollectionAssert.AreEqual(new[] { TimeSpan.FromSeconds(5) }, delays);
        lock (connection) CollectionAssert.AreEqual(new[] { true, false, true }, connection);
    }

    [TestMethod]
    public async Task ARejectedLoginIsRetriedAndThrownAfterThreeAttempts()
    {
        await SeedStoreAsync();
        var server = new FakeMcsServer();
        var delays = new List<TimeSpan>();
        var gate = new TaskCompletionSource();
        await using var client = new EufyPushClient(Options, new FakePushCloud(), NullLogger.Instance, new EufyPushClientHooks
        {
            Connect = server.ConnectAsync,
            Delay = async (d, ct) =>
            {
                lock (delays) delays.Add(d);
                // Hold the bring-up retry so the test sees one failed bring-up.
                if (d >= EufyPushClient.PushRetryMin) await gate.Task.WaitAsync(ct);
            },
        });

        var start = client.StartAsync(CancellationToken.None);
        for (var i = 0; i < 3; i++)
        {
            var stream = await WithTimeout(server.Connections.Reader.ReadAsync().AsTask());
            await WithTimeout(stream.ReadClientMessageAsync());
            stream.ServerSend(LoginResponse(error: new McsErrorInfo { Code = 401, Message = "wrong_secret" }));
        }

        var ex = await Assert.ThrowsExactlyAsync<McsLoginException>(() => WithTimeout(start));
        StringAssert.Contains(ex.Message, "after 3 attempts");
        Assert.IsFalse(client.IsConnected);
        await WaitUntilAsync(() => { lock (delays) return delays.Count == 3; });
        lock (delays) CollectionAssert.AreEqual(new[] { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), EufyPushClient.PushRetryMin }, delays);

        // The background retry brings it up later.
        gate.SetResult();
        var retried = await WithTimeout(server.Connections.Reader.ReadAsync().AsTask());
        await WithTimeout(retried.ReadClientMessageAsync());
        retried.ServerSend(LoginResponse());
        await WaitUntilAsync(() => client.IsConnected);
        await WithTimeout(client.StartAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task RegistersWithFcmWhenThereIsNoFileAndContinuesWhenTheCloudRejectsTheToken()
    {
        var server = new FakeMcsServer();
        var cloud = new FakePushCloud { FailRegistration = true };
        var fcm = new FcmRegistrarTests.FakeFcmHandler();
        await using var client = new EufyPushClient(Options, cloud, NullLogger.Instance, new EufyPushClientHooks
        {
            Connect = server.ConnectAsync,
            HttpHandler = fcm,
            Delay = (_, ct) => Task.Delay(1, ct),
        });

        var start = client.StartAsync(CancellationToken.None);
        var stream = await WithTimeout(server.Connections.Reader.ReadAsync().AsTask());
        var login = (McsLoginRequest)(await WithTimeout(stream.ReadClientMessageAsync())).Message!;
        Assert.AreEqual("5123456789012345678", login.User);
        Assert.AreEqual("17293822569102704642", login.AuthToken);
        stream.ServerSend(LoginResponse());
        await WithTimeout(start);

        CollectionAssert.AreEqual(new[] { "fcm-token-1" }, cloud.RegisteredTokens);
        Assert.HasCount(3, fcm.Requests);
        var saved = await new FileFcmStore(StorePath).LoadAsync(CancellationToken.None);
        Assert.AreEqual("fcm-token-1", saved!.Creds.FcmToken);
        Assert.DoesNotContain("user", Path.GetFileName(StorePath));
    }

    [TestMethod]
    public async Task RequiresACloudLogin()
    {
        await SeedStoreAsync();
        var server = new FakeMcsServer();
        await using var client = new EufyPushClient(Options, new FakePushCloud { UserId = null }, NullLogger.Instance, new EufyPushClientHooks
        {
            Connect = server.ConnectAsync,
            Delay = (_, ct) => Task.Delay(Timeout, ct),
        });
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => WithTimeout(client.StartAsync(CancellationToken.None)));
    }

    [TestMethod]
    public async Task CancellingStartStopsTheClient()
    {
        await SeedStoreAsync();
        var server = new FakeMcsServer();
        await using var client = new EufyPushClient(Options, new FakePushCloud(), NullLogger.Instance, new EufyPushClientHooks { Connect = server.ConnectAsync });
        using var cts = new CancellationTokenSource();

        var start = client.StartAsync(cts.Token);
        var stream = await WithTimeout(server.Connections.Reader.ReadAsync().AsTask());
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => WithTimeout(start));
        await WaitUntilAsync(() => stream.IsDisposed);
        Assert.IsFalse(client.IsConnected);
    }

    [TestMethod]
    public void SerialisesNoCredentialsIntoTheEvent()
    {
        var json = JsonSerializer.Serialize(new EufySecurityEvent { Kind = EufyEventKind.Motion, DeviceSerial = "D" });
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Hands out a new <see cref="FakeMcsStream"/> per dial.</summary>
internal sealed class FakeMcsServer
{
    public Channel<FakeMcsStream> Connections { get; } = Channel.CreateUnbounded<FakeMcsStream>();

    public Task<Stream> ConnectAsync(CancellationToken cancellationToken)
    {
        var stream = new FakeMcsStream();
        Connections.Writer.TryWrite(stream);
        return Task.FromResult<Stream>(stream);
    }
}

/// <summary>A duplex in-memory stream: the test plays the MCS server.</summary>
internal sealed class FakeMcsStream : Stream
{
    private readonly Channel<byte[]> _toClient = Channel.CreateUnbounded<byte[]>();
    private readonly Channel<byte[]> _fromClient = Channel.CreateUnbounded<byte[]>();
    private readonly McsParser _clientParser = new();
    private readonly Queue<McsMessage> _clientMessages = new();
    private byte[] _pending = [];
    private int _pendingOffset;

    public bool IsDisposed { get; private set; }

    public void ServerSend(byte[] bytes) => _toClient.Writer.TryWrite(bytes);

    public void ServerClose() => _toClient.Writer.TryComplete();

    /// <summary>The next frame the client wrote (its first carries the version byte).</summary>
    public async Task<McsMessage> ReadClientMessageAsync()
    {
        while (_clientMessages.Count == 0)
        {
            var chunk = await _fromClient.Reader.ReadAsync();
            foreach (var m in _clientParser.HandleData(chunk)) _clientMessages.Enqueue(m);
        }
        return _clientMessages.Dequeue();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_pendingOffset >= _pending.Length)
        {
            if (!await _toClient.Reader.WaitToReadAsync(cancellationToken)) return 0;
            if (!_toClient.Reader.TryRead(out var next)) return 0;
            _pending = next;
            _pendingOffset = 0;
        }
        var count = Math.Min(buffer.Length, _pending.Length - _pendingOffset);
        _pending.AsMemory(_pendingOffset, count).CopyTo(buffer);
        _pendingOffset += count;
        return count;
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        _fromClient.Writer.TryWrite(buffer.ToArray());
        return ValueTask.CompletedTask;
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    protected override void Dispose(bool disposing)
    {
        IsDisposed = true;
        _toClient.Writer.TryComplete();
        base.Dispose(disposing);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>The two members the push client uses; everything else is out of scope.</summary>
internal sealed class FakePushCloud : IEufyCloudApi
{
    public List<string> RegisteredTokens { get; } = [];
    public bool FailRegistration { get; init; }
    public string? UserId { get; init; } = "user-1";
    public bool IsAuthenticated => UserId is not null;
    public string AccountName => "test";

    public Task RegisterPushTokenAsync(string fcmToken, CancellationToken cancellationToken)
    {
        lock (RegisteredTokens) RegisteredTokens.Add(fcmToken);
        return FailRegistration ? Task.FromException(new EufyCloudException("rejected", 400)) : Task.CompletedTask;
    }

    public Task<EufyLoginResult> LoginAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<EufyLoginResult> SolveCaptchaAsync(string answer, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<EufyLoginResult> SubmitVerifyCodeAsync(string code, CancellationToken cancellationToken) => throw new NotSupportedException();
    public void ClearSession() => throw new NotSupportedException();
    public Task<IReadOnlyList<EufyDeviceRecord>> GetDevicesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<JsonElement> PostAsync(string service, string path, object? body, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<JsonElement> SecurityAppPostAsync(string path, object? body, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<IReadOnlyDictionary<string, (string DskKey, long Expiration)>> GetDskKeysAsync(IReadOnlyCollection<string> stationSerials, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<string> GetDevicePublicKeyAsync(string deviceSerial, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<byte[]> DownloadImageAsync(string url, string? p2pDid, CancellationToken cancellationToken) => throw new NotSupportedException();
    public void Dispose() { }
}
