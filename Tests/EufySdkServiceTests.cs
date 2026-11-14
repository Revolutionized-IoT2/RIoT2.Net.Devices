using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RIoT2.Net.Devices.Catalog;
using RIoT2.Net.Devices.Services.Eufy;
using RIoT2.Net.Devices.Services.EufySdk;
using RIoT2.Net.Devices.Services.Interfaces;

namespace RIoT2.Net.Devices.Tests;

/// <summary>The eufy-sdk service with a fake bridge, and the real bridge bundle run with Node.js.</summary>
[TestClass]
public class EufySdkServiceTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static readonly object[] Devices =
    [
        new { sn = "S1", name = "HomeBase", model = "T8010", stationSn = "S1", p2pDid = "P2P-S1", deviceType = 0, @params = new Dictionary<string, string> { ["1224"] = "1" } },
        new { sn = "T1", name = "Garden", model = "T8113", stationSn = "S1", channel = 0, @params = new Dictionary<string, string>() },
        new { sn = "D1", name = "", stationSn = "D1", p2pDid = "P2P-D1", @params = new Dictionary<string, string>() },
    ];

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task StartRunsTheBridgeLogsInAndListsTheDevices()
    {
        var launcher = new FakeLauncher();
        await using var service = NewService(launcher, out var data);
        var modes = new ConcurrentQueue<(string, int)>();
        service.GuardModeChanged += (station, mode) => modes.Enqueue((station, mode));

        await service.StartAsync(CancellationToken.None);

        Assert.AreEqual(EufyConnectionState.Connected, service.Status.State);
        var bridge = launcher.Bridges.Single();
        CollectionAssert.AreEqual(new[] { "ping", "start", "login", "connect" }, bridge.Methods);
        var start = bridge.Params("start");
        Assert.AreEqual("user@example.com", start.GetProperty("email").GetString());
        Assert.AreEqual("secret", start.GetProperty("password").GetString());
        Assert.AreEqual("FI", start.GetProperty("countryCode").GetString());
        Assert.AreEqual(EufySdkService.InstallId("user@example.com"), start.GetProperty("openudid").GetString());
        Assert.AreNotEqual(EufySdkService.InstallId("other@example.com"), start.GetProperty("openudid").GetString());
        var sessionFile = start.GetProperty("sessionFile").GetString()!;
        Assert.AreEqual(Path.GetFullPath(data), Path.GetDirectoryName(sessionFile));
        Assert.IsFalse(Path.GetFileName(sessionFile).Contains("user", StringComparison.OrdinalIgnoreCase), "the e-mail is hashed");
        StringAssert.StartsWith(Path.GetFileName(start.GetProperty("pushFile").GetString()!), "eufy-sdk-push-");

        Assert.AreEqual(3, service.Devices.Count);
        var station = service.Devices[0];
        Assert.AreEqual(("S1", "HomeBase", "S1", "P2P-S1", 0, "1"), (station.Serial, station.Name, station.StationSerial, station.P2pDid, station.DeviceType, station.Params[1224]));
        Assert.AreEqual(("T1", "S1", 0), (service.Devices[1].Serial, service.Devices[1].StationSerial, service.Devices[1].Channel));
        Assert.AreEqual("D1", service.Devices[2].Name, "a nameless device is named by its serial");
        CollectionAssert.AreEqual(new[] { ("S1", 1) }, modes.ToArray());
    }

    [TestMethod]
    public async Task CaptchaAndVerificationCodeAreAnsweredThroughTheBridge()
    {
        var launcher = new FakeLauncher();
        launcher.Configure = b =>
        {
            b.Results["login"] = new { status = "captcha", image = "data:image/png;base64,AAEC", retry = false };
            b.Results["solveCaptcha"] = new { status = "2fa", method = "email" };
            b.Results["submitVerifyCode"] = new { status = "ok" };
        };
        await using var service = NewService(launcher, out _);

        await service.StartAsync(CancellationToken.None);
        Assert.AreEqual((EufyConnectionState.CaptchaRequired, "data:image/png;base64,AAEC"), (service.Status.State, service.Status.CaptchaImage));

        await service.SubmitVerificationAsync(" ab12 ", CancellationToken.None);
        Assert.AreEqual((EufyConnectionState.TwoFactorRequired, "email"), (service.Status.State, service.Status.TwoFactorMethod));

        await service.SubmitVerificationAsync("123456", CancellationToken.None);
        Assert.AreEqual(EufyConnectionState.Connected, service.Status.State);
        var bridge = launcher.Bridges.Single();
        Assert.AreEqual("ab12", bridge.Params("solveCaptcha").GetProperty("answer").GetString());
        Assert.AreEqual("123456", bridge.Params("submitVerifyCode").GetProperty("code").GetString());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.SubmitVerificationAsync("1", CancellationToken.None));
    }

    [TestMethod]
    public async Task SecurityEventsAreRaisedWithTheirPicturesAndGuardModeAnnouncementsAreApplied()
    {
        var launcher = new FakeLauncher();
        await using var service = NewService(launcher, out _);
        var received = new ConcurrentQueue<(EufySecurityEvent Event, byte[]? Picture)>();
        var done = new TaskCompletionSource();
        service.SecurityEvent += (e, picture) =>
        {
            received.Enqueue((e, picture));
            if (received.Count == 1)
                throw new InvalidOperationException("subscriber failure");
            if (received.Count == 3)
                done.TrySetResult();
        };
        var modes = new ConcurrentQueue<(string, int)>();
        await service.StartAsync(CancellationToken.None);
        service.GuardModeChanged += (station, mode) => modes.Enqueue((station, mode));
        var bridge = launcher.Bridges.Single();

        bridge.Emit("security", new { name = "personDetected", deviceSn = "T1", personName = "Alice", eventType = 3101, picture = Convert.ToBase64String([1, 2, 3]) });
        bridge.Emit("security", new { name = "unknownEvent", deviceSn = "T1" });
        bridge.Emit("security", new { name = "alarm", stationSn = "S1", phase = "delayed", alarmType = 2 });
        bridge.Emit("security", new { name = "doorbellPress", deviceSn = "D1" });
        bridge.Emit("guardMode", new { stationSn = "S1", mode = 63 });
        await done.Task.WaitAsync(Timeout);

        var events = received.ToArray();
        CollectionAssert.AreEqual(new[] { EufyEventKind.PersonDetected, EufyEventKind.Alarm, EufyEventKind.DoorbellPress }, events.Select(e => e.Event.Kind).ToArray());
        Assert.AreEqual(("T1", "Alice", 3101), (events[0].Event.DeviceSerial, events[0].Event.PersonName, events[0].Event.EventType));
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, events[0].Picture);
        Assert.AreEqual(("S1", EufyAlarmPhase.Delayed, 2), (events[1].Event.StationSerial, events[1].Event.AlarmPhase, events[1].Event.AlarmType));
        Assert.IsNull(events[2].Picture);
        CollectionAssert.AreEqual(new[] { ("S1", 63) }, modes.ToArray());
        Assert.AreEqual(63, service.GetGuardMode("S1"));
    }

    [TestMethod]
    public async Task AnArmingModeChangeReadsTheDeviceListAgain()
    {
        var launcher = new FakeLauncher();
        await using var service = NewService(launcher, out _);
        await service.StartAsync(CancellationToken.None);
        var bridge = launcher.Bridges.Single();
        var changed = new TaskCompletionSource<(string, int)>();
        service.GuardModeChanged += (station, mode) => changed.TrySetResult((station, mode));

        bridge.Results["getDevices"] = new { devices = new object[] { new { sn = "S1", name = "HomeBase", stationSn = "S1", @params = new Dictionary<string, string> { ["1224"] = "0" } } } };
        bridge.Emit("security", new { name = "armingModeChanged", deviceSn = "S1" });

        Assert.AreEqual(("S1", 0), await changed.Task.WaitAsync(Timeout));
        Assert.AreEqual(1, service.Devices.Count);
    }

    [TestMethod]
    public async Task GuardModeAndSnapshotGoThroughTheBridgeAndItsErrorsAreThrown()
    {
        var launcher = new FakeLauncher();
        await using var service = NewService(launcher, out _);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.SetGuardModeAsync("S1", 0, CancellationToken.None));
        await service.StartAsync(CancellationToken.None);
        var bridge = launcher.Bridges.Single();
        bridge.Results["snapshot"] = new { jpeg = Convert.ToBase64String([0xFF, 0xD8]), retained = false };

        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => service.SetGuardModeAsync("S1", 7, CancellationToken.None));
        await service.SetGuardModeAsync("S1", 0, CancellationToken.None);
        var jpeg = await service.SnapshotAsync("T1", CancellationToken.None);

        var setMode = bridge.Params("setGuardMode");
        Assert.AreEqual(("S1", 0), (setMode.GetProperty("stationSn").GetString(), setMode.GetProperty("mode").GetInt32()));
        Assert.AreEqual(0, service.GetGuardMode("S1"));
        CollectionAssert.AreEqual(new byte[] { 0xFF, 0xD8 }, jpeg);

        bridge.Errors["snapshot"] = ("LiveSnapshotUnavailableError", "the camera is asleep");
        var error = await Assert.ThrowsExactlyAsync<EufySdkException>(() => service.SnapshotAsync("T1", CancellationToken.None));
        Assert.AreEqual(("LiveSnapshotUnavailableError", "the camera is asleep"), (error.ErrorName, error.Message));
    }

    [TestMethod]
    public async Task AFailedLoginLeavesNothingRunningAndTheServiceStartsAgain()
    {
        var launcher = new FakeLauncher { Configure = b => b.Errors["login"] = ("Error", "wrong password") };
        await using var service = NewService(launcher, out _);

        var error = await Assert.ThrowsExactlyAsync<EufySdkException>(() => service.StartAsync(CancellationToken.None));
        Assert.AreEqual("wrong password", error.Message);
        Assert.AreEqual(EufyConnectionState.Error, service.Status.State);
        Assert.IsTrue(launcher.Bridges.Single().Disposed);

        launcher.Configure = null;
        await service.StartAsync(CancellationToken.None);
        Assert.AreEqual(EufyConnectionState.Connected, service.Status.State);
    }

    [TestMethod]
    public async Task ABridgeThatExitsIsRestartedAndAPendingRequestFails()
    {
        var launcher = new FakeLauncher();
        await using var service = NewService(launcher, out _);
        await service.StartAsync(CancellationToken.None);
        var first = launcher.Bridges.Single();
        first.Hold.Add("snapshot");
        var connected = new TaskCompletionSource();
        service.StatusChanged += s =>
        {
            if (s.State == EufyConnectionState.Connected)
                connected.TrySetResult();
        };

        var snapshot = service.SnapshotAsync("T1", CancellationToken.None);
        await first.Received("snapshot").WaitAsync(Timeout);
        first.Exit(1);

        var error = await Assert.ThrowsExactlyAsync<EufySdkException>(() => snapshot);
        StringAssert.Contains(error.Message, "exit code 1");
        await connected.Task.WaitAsync(Timeout);
        Assert.AreEqual(2, launcher.Bridges.Count);
        CollectionAssert.AreEqual(new[] { "ping", "start", "login", "connect" }, launcher.Bridges[1].Methods);
    }

    [TestMethod]
    public async Task StopClosesTheBridge()
    {
        var launcher = new FakeLauncher();
        var service = NewService(launcher, out _);
        await service.StartAsync(CancellationToken.None);

        await service.DisposeAsync();

        Assert.IsTrue(launcher.Bridges.Single().Disposed);
        Assert.AreEqual(EufyConnectionState.Stopped, service.Status.State);
        Assert.AreEqual(0, service.Devices.Count);
        await Task.Delay(50);
        Assert.AreEqual(1, launcher.Bridges.Count, "a stopped service doesn't restart the bridge");
    }

    [TestMethod]
    public void EventNamesMapToTheKindsTheDevicesReport()
    {
        foreach (var kind in Enum.GetValues<EufyEventKind>())
            Assert.IsTrue(EufySdkService.EventKinds.Values.Contains(kind), $"{kind} has an eufy-sdk event");
        var alarm = EufySdkService.ToSecurityEvent(JsonElement.Parse("""{"name":"alarm","stationSn":"S1"}"""));
        Assert.AreEqual(EufyAlarmPhase.Triggered, alarm!.Value.Event.AlarmPhase, "an alarm without a phase was triggered");
        Assert.IsNull(EufySdkService.ToSecurityEvent(JsonElement.Parse("""{"name":"propertyChanged"}""")));
        Assert.IsNull(EufySdkService.ToRecord(JsonElement.Parse("""{"name":"no serial"}""")));
    }

    [TestMethod]
    public void TheSdkDeviceSharesTheTemplatesOfTheEufySecurityDevice()
    {
        var service = new StaticService();
        var sdkDevice = new EufySecuritySdk(NullLogger.Instance, service, null!);
        var portDevice = new EufySecurity(NullLogger.Instance, service, null!);

        var sdkTemplate = sdkDevice.GetConfigurationTemplate();
        var portTemplate = portDevice.GetConfigurationTemplate();

        Assert.AreEqual("Eufy Security (eufy-sdk)", sdkTemplate.Name);
        Assert.AreEqual(typeof(EufySecuritySdk).FullName, sdkTemplate.ClassFullName);
        CollectionAssert.AreEqual(portTemplate.ReportTemplates!.Select(r => r.Address).ToArray(), sdkTemplate.ReportTemplates!.Select(r => r.Address).ToArray());
        CollectionAssert.AreEqual(portTemplate.CommandTemplates!.Select(c => c.Address).ToArray(), sdkTemplate.CommandTemplates!.Select(c => c.Address).ToArray());
        CollectionAssert.AreEqual(portTemplate.DeviceParameters.Keys.ToArray(), sdkTemplate.DeviceParameters.Keys.ToArray());
    }

    [TestMethod]
    public async Task TheBundleIsExtractedOnceAndOlderOnesAreRemoved()
    {
        var data = Path.Combine(Path.GetTempPath(), "riot2-eufy-sdk-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(data, "eufy-sdk"));
            var old = Path.Combine(data, "eufy-sdk", "bridge-0000000000000000.mjs");
            await File.WriteAllTextAsync(old, "old", TestContext.CancellationToken);

            var path = await NodeBridgeLauncher.ExtractBundleAsync(data, TestContext.CancellationToken);
            var written = File.GetLastWriteTimeUtc(path);
            Assert.AreEqual(path, await NodeBridgeLauncher.ExtractBundleAsync(data, TestContext.CancellationToken));

            Assert.IsFalse(File.Exists(old));
            Assert.AreEqual(written, File.GetLastWriteTimeUtc(path), "an extracted bundle is not written again");
            StringAssert.StartsWith(await File.ReadAllTextAsync(path, TestContext.CancellationToken), "// RIoT2 eufy-sdk bridge with @mega-yfue/eufy-sdk ");
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
    }

    /// <summary>Runs the embedded bundle with the Node.js on the PATH; inconclusive without Node.js 24.5.</summary>
    [TestMethod]
    public async Task TheEmbeddedBundleRunsWithNode()
    {
        if (!NodeAvailable(out var version))
            Assert.Inconclusive($"Node.js 24.5 or newer is not on the PATH ({version}).");

        var data = Path.Combine(Path.GetTempPath(), "riot2-eufy-sdk-" + Guid.NewGuid().ToString("N"));
        try
        {
            var connection = await new NodeBridgeLauncher(NullLogger.Instance).StartAsync(data, TestContext.CancellationToken);
            var client = new EufyBridgeClient(connection, NullLogger.Instance);
            try
            {
                var ping = await client.InvokeAsync("ping", null, Timeout, TestContext.CancellationToken);
                Assert.AreEqual("0.5.0", ping.GetProperty("sdk").GetString());
                Assert.AreEqual(version, "v" + ping.GetProperty("node").GetString());

                var unknown = await Assert.ThrowsExactlyAsync<EufySdkException>(() => client.InvokeAsync("nope", null, Timeout, TestContext.CancellationToken));
                StringAssert.Contains(unknown.Message, "unknown method");
                var early = await Assert.ThrowsExactlyAsync<EufySdkException>(() => client.InvokeAsync("login", null, Timeout, TestContext.CancellationToken));
                StringAssert.Contains(early.Message, "start first");
            }
            finally
            {
                await client.DisposeAsync();
            }

            Assert.AreEqual(0, await client.Exited.WaitAsync(Timeout, TestContext.CancellationToken), "a closed stdin stops the bridge");
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
    }

    private static bool NodeAvailable(out string version)
    {
        version = "not found";
        try
        {
            using var process = Process.Start(new ProcessStartInfo("node", "--version") { RedirectStandardOutput = true, UseShellExecute = false })!;
            version = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(10_000);
            return Version.TryParse(version.TrimStart('v'), out var v) && v >= new Version(24, 5);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static EufySdkService NewService(FakeLauncher launcher, out string data)
    {
        data = Path.Combine(Path.GetTempPath(), "riot2-eufy-sdk-test");
        var service = new EufySdkService(NullLogger.Instance, launcher, TimeSpan.Zero, TimeSpan.FromMilliseconds(10), TimeProvider.System);
        service.Configure(new EufyAccountOptions { Email = "user@example.com", Password = "secret", CountryCode = "FI", DataDirectory = data });
        return service;
    }

    private sealed class FakeLauncher : IEufyBridgeLauncher
    {
        private readonly List<FakeBridge> _bridges = [];
        public Action<FakeBridge>? Configure { get; set; }
        public List<FakeBridge> Bridges
        {
            get
            {
                lock (_bridges)
                    return [.. _bridges];
            }
        }

        public Task<IEufyBridgeConnection> StartAsync(string dataDirectory, CancellationToken cancellationToken)
        {
            var bridge = new FakeBridge();
            bridge.Results["ping"] = new { node = "24.5.0", sdk = "0.5.0" };
            bridge.Results["start"] = new { };
            bridge.Results["login"] = new { status = "ok" };
            bridge.Results["connect"] = new { devices = Devices, realtime = "ready" };
            bridge.Results["getDevices"] = new { devices = Devices };
            bridge.Results["setGuardMode"] = new { };
            Configure?.Invoke(bridge);
            lock (_bridges)
                _bridges.Add(bridge);
            return Task.FromResult<IEufyBridgeConnection>(bridge);
        }
    }

    /// <summary>Answers requests from canned results, like the bridge script would.</summary>
    private sealed class FakeBridge : IEufyBridgeConnection
    {
        private readonly Channel<string> _output = Channel.CreateUnbounded<string>();
        private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentQueue<(string Method, JsonElement Params)> _requests = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _received = new();

        public ConcurrentDictionary<string, object> Results { get; } = new();
        public ConcurrentDictionary<string, (string Name, string Message)> Errors { get; } = new();
        /// <summary>Methods that are received but never answered.</summary>
        public HashSet<string> Hold { get; } = [];
        public bool Disposed { get; private set; }
        public ChannelReader<string> Output => _output.Reader;
        public Task<int> Exited => _exited.Task;
        public string[] Methods => _requests.Select(r => r.Method).ToArray();

        public JsonElement Params(string method) => _requests.Last(r => r.Method == method).Params;

        public Task Received(string method) => _received.GetOrAdd(method, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

        public Task WriteLineAsync(string line, CancellationToken cancellationToken)
        {
            if (_exited.Task.IsCompleted)
                throw new IOException("The pipe is closed.");

            using var document = JsonDocument.Parse(line);
            var request = document.RootElement;
            var id = request.GetProperty("id").GetInt64();
            var method = request.GetProperty("method").GetString()!;
            _requests.Enqueue((method, request.GetProperty("params").Clone()));
            _received.GetOrAdd(method, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();

            if (Hold.Contains(method))
                return Task.CompletedTask;
            if (Errors.TryGetValue(method, out var error))
                Write(new { id, error = new { name = error.Name, message = error.Message } });
            else if (Results.TryGetValue(method, out var result))
                Write(new { id, result });
            else
                Write(new { id, error = new { name = "Error", message = $"unknown method \"{method}\"" } });
            return Task.CompletedTask;
        }

        public void Emit(string name, object data) => Write(new { @event = name, data });

        public void Exit(int code)
        {
            _output.Writer.TryComplete();
            _exited.TrySetResult(code);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            Exit(0);
            return ValueTask.CompletedTask;
        }

        private void Write(object message) => _output.Writer.TryWrite(JsonSerializer.Serialize(message));
    }

    private sealed class StaticService : IEufySdkService
    {
        public EufyServiceStatus Status => new(EufyConnectionState.Connected);
        public IReadOnlyList<EufyDeviceRecord> Devices { get; } =
        [
            new() { Serial = "S1", Name = "HomeBase", StationSerial = "S1" },
            new() { Serial = "T1", Name = "Garden", StationSerial = "S1" },
        ];
#pragma warning disable CS0067 // The template test raises no events.
        public event Action<EufySecurityEvent, byte[]?>? SecurityEvent;
        public event Action<string, int>? GuardModeChanged;
        public event Action<EufyServiceStatus>? StatusChanged;
#pragma warning restore CS0067
        public void Configure(EufyAccountOptions options) { }
        public int? GetGuardMode(string stationSerial) => null;
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public Task SubmitVerificationAsync(string answer, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RefreshAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SetGuardModeAsync(string stationSerial, int mode, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<byte[]> SnapshotAsync(string deviceSerial, CancellationToken cancellationToken) => Task.FromResult(Array.Empty<byte>());
    }
}
