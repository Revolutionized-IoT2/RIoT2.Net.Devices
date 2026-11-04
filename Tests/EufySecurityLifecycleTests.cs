using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RIoT2.Core.Models;
using RIoT2.Net.Devices.Catalog;
using RIoT2.Net.Devices.Models;
using RIoT2.Net.Devices.Services;
using RIoT2.Net.Devices.Services.Eufy;
using RIoT2.Net.Devices.Services.Interfaces;

namespace RIoT2.Net.Devices.Tests;

/// <summary>The Eufy Security service facade and device, with fake cloud, push and P2P clients.</summary>
[TestClass]
public class EufySecurityLifecycleTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static EufyAccountOptions Account => new() { Email = "user@example.com", Password = "secret", DataDirectory = "unused" };

    [TestMethod]
    public async Task ServiceLogsInReadsTheGuardModesAndStartsPush()
    {
        var factory = new FakeFactory();
        await using var service = NewService(factory);
        var modes = new ConcurrentQueue<(string, int)>();
        service.GuardModeChanged += (station, mode) => modes.Enqueue((station, mode));

        await service.StartAsync(CancellationToken.None);
        await factory.Push.Started.Task.WaitAsync(Timeout);

        Assert.AreEqual(EufyConnectionState.Connected, service.Status.State);
        CollectionAssert.AreEqual(new[] { ("S1", 1) }, modes.ToArray());
        Assert.AreEqual(1, service.GetGuardMode("S1"));
        Assert.AreEqual(3, factory.P2P.Devices.Count);
        Assert.AreEqual(3, service.Devices.Count);
    }

    [TestMethod]
    public async Task CaptchaAndVerificationCodeAreAnsweredThenTheServiceConnects()
    {
        var factory = new FakeFactory();
        factory.Cloud.Login = new EufyLoginResult(EufyLoginStatus.Captcha, "data:image/png;base64,AAEC");
        factory.Cloud.Captcha = new EufyLoginResult(EufyLoginStatus.TwoFactor, TwoFactorMethod: "email");
        await using var service = NewService(factory);

        await service.StartAsync(CancellationToken.None);
        Assert.AreEqual(EufyConnectionState.CaptchaRequired, service.Status.State);
        Assert.AreEqual("data:image/png;base64,AAEC", service.Status.CaptchaImage);

        await service.SubmitVerificationAsync(" ab12 ", CancellationToken.None);
        Assert.AreEqual(EufyConnectionState.TwoFactorRequired, service.Status.State);
        Assert.AreEqual("email", service.Status.TwoFactorMethod);

        await service.SubmitVerificationAsync("123456", CancellationToken.None);
        Assert.AreEqual(EufyConnectionState.Connected, service.Status.State);
        CollectionAssert.AreEqual(new[] { "captcha:ab12", "code:123456" }, factory.Cloud.Answers);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.SubmitVerificationAsync("1", CancellationToken.None));
    }

    [TestMethod]
    public async Task AFailedLoginLeavesNothingRunningAndTheServiceStartsAgain()
    {
        var factory = new FakeFactory();
        factory.Cloud.LoginError = new EufyCloudException("wrong password", 26006);
        await using var service = NewService(factory);

        await Assert.ThrowsExactlyAsync<EufyCloudException>(() => service.StartAsync(CancellationToken.None));
        Assert.AreEqual(EufyConnectionState.Error, service.Status.State);
        Assert.IsTrue(factory.Cloud.Disposed);

        factory.Cloud = new FakeCloud();
        await service.StartAsync(CancellationToken.None);
        Assert.AreEqual(EufyConnectionState.Connected, service.Status.State);
    }

    [TestMethod]
    public async Task EventsAreRaisedInOrderWithTheirThumbnailsAndAFailingHandlerDoesNotStopTheNext()
    {
        var factory = new FakeFactory();
        factory.Cloud.Images["https://img/1"] = [1, 2, 3];
        await using var service = NewService(factory);
        var received = new List<(EufyEventKind Kind, byte[]? Picture)>();
        var done = new TaskCompletionSource();
        service.SecurityEvent += (e, picture) =>
        {
            received.Add((e.Kind, picture));
            if (received.Count == 1)
                throw new InvalidOperationException("subscriber failure");
            if (received.Count == 3)
                done.TrySetResult();
        };
        await service.StartAsync(CancellationToken.None);

        factory.Push.Raise(new EufySecurityEvent { Kind = EufyEventKind.Motion, DeviceSerial = "T1", ThumbnailUrl = "https://img/1" });
        factory.Push.Raise(new EufySecurityEvent { Kind = EufyEventKind.PersonDetected, DeviceSerial = "T1", ThumbnailUrl = "https://img/missing" });
        factory.Push.Raise(new EufySecurityEvent { Kind = EufyEventKind.DoorbellPress, DeviceSerial = "D1" });
        await done.Task.WaitAsync(Timeout);

        CollectionAssert.AreEqual(new[] { EufyEventKind.Motion, EufyEventKind.PersonDetected, EufyEventKind.DoorbellPress }, received.Select(r => r.Kind).ToArray());
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, received[0].Picture);
        Assert.IsNull(received[1].Picture, "A failed download reports the event without a picture.");
        CollectionAssert.AreEqual(new[] { "https://img/1|P2P-S1", "https://img/missing|P2P-S1" }, factory.Cloud.Downloads.ToArray());
    }

    [TestMethod]
    public async Task AnArmingModeChangeReadsTheGuardModeAgain()
    {
        var factory = new FakeFactory();
        await using var service = NewService(factory);
        var changed = new TaskCompletionSource<(string, int)>();
        await service.StartAsync(CancellationToken.None);
        service.GuardModeChanged += (station, mode) => changed.TrySetResult((station, mode));

        factory.Cloud.GuardMode = "63";
        factory.Push.Raise(new EufySecurityEvent { Kind = EufyEventKind.ArmingModeChanged, StationSerial = "S1" });

        Assert.AreEqual(("S1", 63), await changed.Task.WaitAsync(Timeout));
    }

    [TestMethod]
    public async Task GuardModeAndSnapshotGoThroughP2P()
    {
        var factory = new FakeFactory();
        await using var service = NewService(factory);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.SetGuardModeAsync("S1", 0, CancellationToken.None));
        await service.StartAsync(CancellationToken.None);
        var modes = new List<(string, int)>();
        service.GuardModeChanged += (station, mode) => modes.Add((station, mode));

        await service.SetGuardModeAsync("S1", 0, CancellationToken.None);
        var jpeg = await service.SnapshotAsync("T1", CancellationToken.None);

        CollectionAssert.AreEqual(new[] { ("S1", 0) }, factory.P2P.GuardModes);
        CollectionAssert.AreEqual(new[] { ("S1", 0) }, modes);
        CollectionAssert.AreEqual(new byte[] { 0xFF, 0xD8 }, jpeg);
    }

    [TestMethod]
    public async Task StopClosesTheClients()
    {
        var factory = new FakeFactory();
        var service = NewService(factory);
        await service.StartAsync(CancellationToken.None);
        await factory.Push.Started.Task.WaitAsync(Timeout);

        await service.DisposeAsync();

        Assert.AreEqual(EufyConnectionState.Stopped, service.Status.State);
        Assert.IsTrue(factory.Push.Stopped);
        Assert.IsTrue(factory.P2P.Closed);
        Assert.IsTrue(factory.Cloud.Disposed);
        Assert.AreEqual(0, service.Devices.Count);
    }

    [TestMethod]
    public void TemplateListsStationsCamerasAndTheAccountCommands()
    {
        var service = new FakeEufyService();
        var device = new EufySecurity(NullLogger.Instance, service, new FakeStorage());

        var template = device.GetConfigurationTemplate();

        CollectionAssert.AreEquivalent(new[] { EufySecurity.StatusAddress, "S1|guardMode", "S1|alarm", "D1|guardMode", "D1|alarm", "T1", "D1" },
            template.ReportTemplates!.Select(r => r.Address).ToArray());
        CollectionAssert.AreEquivalent(new[] { EufySecurity.VerifyAddress, "S1|guardMode", "D1|guardMode", "T1|snapshot", "D1|snapshot" },
            template.CommandTemplates!.Select(c => c.Address).ToArray());
        CollectionAssert.AreEquivalent(new[] { "email", "password", "countryCode", "ffmpegPath" }, template.DeviceParameters.Keys.ToArray());
    }

    [TestMethod]
    public async Task DeviceReportsStatusGuardModesAndEventsAndRunsCommands()
    {
        var service = new FakeEufyService();
        var storage = new FakeStorage();
        var device = new EufySecurity(NullLogger.Instance, service, storage);
        device.Initialize(Configured(device));
        var reports = new List<(string Address, string Value)>();
        device.ReportUpdated += (_, r) =>
        {
            var report = (Report)r;
            reports.Add((device.ReportTemplates!.Single(t => t.Id == report.Id).Address, report.Value.ToJson()));
        };
        string Command(string address) => device.CommandTemplates!.Single(c => c.Address == address).Id;

        await device.StartAsync(CancellationToken.None);
        service.RaiseEvent(new EufySecurityEvent { Kind = EufyEventKind.PersonDetected, DeviceSerial = "T1", PersonName = "Alice" }, [9]);
        service.RaiseEvent(new EufySecurityEvent { Kind = EufyEventKind.Alarm, DeviceSerial = "T1", AlarmPhase = EufyAlarmPhase.Delayed, AlarmType = 2 }, null);
        await device.ExecuteCommandAsync(Command("S1|guardMode"), "63", CancellationToken.None);
        await device.ExecuteCommandAsync(Command(EufySecurity.VerifyAddress), "123456", CancellationToken.None);
        await device.ExecuteCommandAsync(Command("D1|snapshot"), "true", CancellationToken.None);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => device.ExecuteCommandAsync(Command("S1|guardMode"), "home", CancellationToken.None));
        await device.StopAsync(CancellationToken.None);

        Assert.AreEqual((1, 1), (service.Starts, service.Stops));
        Assert.IsFalse(service.HasSubscribers);
        CollectionAssert.AreEqual(new[] { EufySecurity.StatusAddress, "S1|guardMode", "T1", "S1|alarm", "S1|guardMode", "D1" }, reports.Select(r => r.Address).ToArray());
        StringAssert.Contains(reports[0].Value, "Connected");
        Assert.AreEqual(new ValueModel(1).ToJson(), reports[1].Value);
        StringAssert.Contains(reports[2].Value, "Alice");
        StringAssert.Contains(reports[2].Value, "memory://");
        StringAssert.Contains(reports[3].Value, "delayed");
        Assert.AreEqual(new ValueModel(63).ToJson(), reports[4].Value);
        StringAssert.Contains(reports[5].Value, "memory://");
        CollectionAssert.AreEqual(new[] { "123456" }, service.Answers);
        CollectionAssert.AreEqual(new[] { "T1", "D1" }, storage.Saved.Select(s => device.ReportTemplates!.Single(t => t.Id == s).Address).ToArray());
    }

    [TestMethod]
    public async Task DeviceReportsAPendingCaptchaWithItsImage()
    {
        var service = new FakeEufyService
        {
            StartStatus = new EufyServiceStatus(EufyConnectionState.CaptchaRequired, "answer", CaptchaImage: "data:image/png;base64,AAEC")
        };
        var storage = new FakeStorage();
        var device = new EufySecurity(NullLogger.Instance, service, storage);
        device.Initialize(Configured(device));
        var reports = new List<string>();
        device.ReportUpdated += (_, r) => reports.Add(((Report)r).Value.ToJson());

        await device.StartAsync(CancellationToken.None);

        Assert.AreEqual(RIoT2.Core.DeviceState.Running, device.State);
        Assert.AreEqual(1, reports.Count);
        StringAssert.Contains(reports[0], "CaptchaRequired");
        StringAssert.Contains(reports[0], "memory://");
        CollectionAssert.AreEqual(new byte[] { 0, 1, 2 }, storage.Data.Single());
        await device.StopAsync(CancellationToken.None);
    }

    [TestMethod]
    [DataRow(EufyEventKind.Motion, SecurityEventType.motionDetected)]
    [DataRow(EufyEventKind.StrangerDetected, SecurityEventType.strangerPersonDetected)]
    [DataRow(EufyEventKind.PetDetected, SecurityEventType.animalDetected)]
    [DataRow(EufyEventKind.DogDetected, SecurityEventType.animalDetected)]
    [DataRow(EufyEventKind.SoundDetected, SecurityEventType.soundDetected)]
    [DataRow(EufyEventKind.CryingDetected, SecurityEventType.cryingDetected)]
    [DataRow(EufyEventKind.VehicleDetected, SecurityEventType.vehicleDetected)]
    [DataRow(EufyEventKind.DoorbellPress, SecurityEventType.doorbellPressed)]
    [DataRow(EufyEventKind.PackageDelivered, SecurityEventType.packageDelivered)]
    [DataRow(EufyEventKind.PackageTaken, SecurityEventType.packageTaken)]
    [DataRow(EufyEventKind.PackageStranded, SecurityEventType.packageStranded)]
    public void EventKindsMapToSecurityEventTypes(EufyEventKind kind, SecurityEventType expected)
    {
        Assert.IsTrue(EufySecurity.TryMap(new EufySecurityEvent { Kind = kind }, out var type, out _, out var message));
        Assert.AreEqual(expected, type);
        Assert.IsFalse(string.IsNullOrEmpty(message));
        Assert.IsFalse(EufySecurity.TryMap(new EufySecurityEvent { Kind = EufyEventKind.ArmingModeChanged }, out _, out _, out _));
    }

    private static DeviceConfiguration Configured(EufySecurity device)
    {
        var configuration = device.GetConfigurationTemplate();
        configuration.DeviceParameters["email"] = "user@example.com";
        configuration.DeviceParameters["password"] = "secret";
        return configuration;
    }

    private static EufySecurityService NewService(FakeFactory factory)
    {
        var service = new EufySecurityService(NullLogger.Instance, factory, TimeSpan.Zero);
        service.Configure(Account);
        return service;
    }

    internal static IReadOnlyList<EufyDeviceRecord> SampleDevices(string guardMode = "1") =>
    [
        new() { Serial = "S1", Name = "HomeBase", StationSerial = "S1", P2pDid = "P2P-S1", Params = new Dictionary<int, string> { [EufySecurityService.GuardModeParam] = guardMode } },
        new() { Serial = "T1", Name = "Garden", StationSerial = "S1", Channel = 0 },
        new() { Serial = "D1", Name = "Doorbell", StationSerial = "D1", P2pDid = "P2P-D1" },
    ];

    private sealed class FakeFactory : IEufyComponentFactory
    {
        public FakeCloud Cloud { get; set; } = new();
        public FakePush Push { get; } = new();
        public FakeP2P P2P { get; } = new();
        public IEufyCloudApi CreateCloud(EufyAccountOptions options, ILogger logger) => Cloud;
        public IEufyPushClient CreatePush(EufyAccountOptions options, IEufyCloudApi cloud, ILogger logger) => Push;
        public IEufyP2PClient CreateP2P(EufyAccountOptions options, IEufyCloudApi cloud, ILogger logger) => P2P;
    }

    private sealed class FakeCloud : IEufyCloudApi
    {
        public EufyLoginResult Login { get; set; } = new(EufyLoginStatus.Ok);
        public EufyLoginResult Captcha { get; set; } = new(EufyLoginStatus.Ok);
        public Exception? LoginError { get; set; }
        public string GuardMode { get; set; } = "1";
        public Dictionary<string, byte[]> Images { get; } = [];
        public ConcurrentQueue<string> Downloads { get; } = new();
        public List<string> Answers { get; } = [];
        public bool Disposed { get; private set; }
        public bool IsAuthenticated => true;
        public string? UserId => "user";
        public string AccountName => "user";

        public Task<EufyLoginResult> LoginAsync(CancellationToken cancellationToken) =>
            LoginError != null ? Task.FromException<EufyLoginResult>(LoginError) : Task.FromResult(Login);
        public Task<EufyLoginResult> SolveCaptchaAsync(string answer, CancellationToken cancellationToken)
        {
            Answers.Add("captcha:" + answer);
            return Task.FromResult(Captcha);
        }
        public Task<EufyLoginResult> SubmitVerifyCodeAsync(string code, CancellationToken cancellationToken)
        {
            Answers.Add("code:" + code);
            return Task.FromResult(new EufyLoginResult(EufyLoginStatus.Ok));
        }
        public void ClearSession() { }
        public Task<IReadOnlyList<EufyDeviceRecord>> GetDevicesAsync(CancellationToken cancellationToken) => Task.FromResult(SampleDevices(GuardMode));
        public Task<JsonElement> PostAsync(string service, string path, object? body, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<JsonElement> SecurityAppPostAsync(string path, object? body, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<string, (string DskKey, long Expiration)>> GetDskKeysAsync(IReadOnlyCollection<string> stationSerials, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string> GetDevicePublicKeyAsync(string deviceSerial, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RegisterPushTokenAsync(string fcmToken, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<byte[]> DownloadImageAsync(string url, string? p2pDid, CancellationToken cancellationToken)
        {
            Downloads.Enqueue(url + "|" + p2pDid);
            return Images.TryGetValue(url, out var image) ? Task.FromResult(image) : Task.FromException<byte[]>(new EufyCloudException("not found", status: 404));
        }
        public void Dispose() => Disposed = true;
    }

    private sealed class FakePush : IEufyPushClient
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Stopped { get; private set; }
        public bool IsConnected => Started.Task.IsCompleted;
        public event Action<EufySecurityEvent>? EventReceived;
        public event Action<bool>? ConnectionChanged;
        public void Raise(EufySecurityEvent e) => EventReceived?.Invoke(e);
        public Task StartAsync(CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            ConnectionChanged?.Invoke(true);
            return Task.CompletedTask;
        }
        public Task StopAsync()
        {
            Stopped = true;
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeP2P : IEufyP2PClient
    {
        public IReadOnlyList<EufyDeviceRecord> Devices { get; private set; } = [];
        public List<(string, int)> GuardModes { get; } = [];
        public bool Closed { get; private set; }
        public void UpdateDevices(IReadOnlyList<EufyDeviceRecord> devices) => Devices = devices;
        public Task SetGuardModeAsync(string stationSerial, int wireMode, CancellationToken cancellationToken)
        {
            GuardModes.Add((stationSerial, wireMode));
            return Task.CompletedTask;
        }
        public Task<byte[]> SnapshotAsync(string deviceSerial, CancellationToken cancellationToken) => Task.FromResult(new byte[] { 0xFF, 0xD8 });
        public Task CloseAsync()
        {
            Closed = true;
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeEufyService : IEufySecurityService
    {
        public int Starts;
        public int Stops;
        public List<string> Answers { get; } = [];
        public EufyServiceStatus StartStatus { get; init; } = new(EufyConnectionState.Connected);
        public EufyServiceStatus Status { get; private set; } = new(EufyConnectionState.Stopped);
        public IReadOnlyList<EufyDeviceRecord> Devices { get; } = SampleDevices();
        public bool HasSubscribers => SecurityEvent != null || GuardModeChanged != null || StatusChanged != null;
        public event Action<EufySecurityEvent, byte[]?>? SecurityEvent;
        public event Action<string, int>? GuardModeChanged;
        public event Action<EufyServiceStatus>? StatusChanged;
        public void Configure(EufyAccountOptions options) { }
        public int? GetGuardMode(string stationSerial) => 1;
        public void RaiseEvent(EufySecurityEvent e, byte[]? picture) => SecurityEvent?.Invoke(e, picture);
        public Task StartAsync(CancellationToken cancellationToken)
        {
            Starts++;
            Status = StartStatus;
            StatusChanged?.Invoke(Status);
            if (Status.State == EufyConnectionState.Connected)
                GuardModeChanged?.Invoke("S1", 1);
            return Task.CompletedTask;
        }
        public Task StopAsync()
        {
            Stops++;
            return Task.CompletedTask;
        }
        public Task SubmitVerificationAsync(string answer, CancellationToken cancellationToken)
        {
            Answers.Add(answer);
            return Task.CompletedTask;
        }
        public Task RefreshAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SetGuardModeAsync(string stationSerial, int mode, CancellationToken cancellationToken)
        {
            GuardModeChanged?.Invoke(stationSerial, mode);
            return Task.CompletedTask;
        }
        public Task<byte[]> SnapshotAsync(string deviceSerial, CancellationToken cancellationToken) => Task.FromResult(new byte[] { 0xFF, 0xD8 });
    }

    private sealed class FakeStorage : IMemoryStorageService
    {
        public List<string> Saved { get; } = [];
        public List<byte[]> Data { get; } = [];
        public string Save(Document document, string address)
        {
            Saved.Add(address);
            Data.Add(document.Data);
            return $"memory://{address}/{document.Filename}";
        }
        public Document? Get(string filename, string address = "") => null;
        public Document? GetLatest(string address) => null;
        public List<DocumentMetadata>? List(string address) => null;
        public void DeleteAddress(string address) { }
        public List<MemoryStorageAddess> GetAllDocuments() => [];
    }
}
