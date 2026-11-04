using System.Collections.Concurrent;
using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RIoT2.Core;
using RIoT2.Core.Models;
using RIoT2.Core.Utils;
using RIoT2.Net.Devices.Catalog;
using RIoT2.Net.Devices.Models;
using RIoT2.Net.Devices.Services;
using RIoT2.Net.Devices.Services.FTP;
using RIoT2.Net.Devices.Services.Interfaces;

namespace RIoT2.Net.Devices.Tests;

/// <summary>
/// The devices moved to <c>AsyncDeviceBase</c> in I31 (plan M11 step 3): their I/O is awaited, so a start,
/// refresh or command doesn't block the scheduler or the node while it waits, and a cancellation reaches the call.
/// </summary>
[TestClass]
public class DeviceLifecycleTests
{
    // CreateDataDirectory sets it before each test.
    private string _dataDirectory = null!;

    [TestInitialize]
    public void CreateDataDirectory()
    {
        _dataDirectory = Path.Combine(AppContext.BaseDirectory, "TestData", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDirectory);
    }

    [TestCleanup]
    public void DeleteDataDirectory() => Directory.Delete(_dataDirectory, true);

    [TestMethod]
    public async Task WaterConsumptionAwaitsTheMeterApiAndHonoursCancellation()
    {
        var http = new FakeHttp();
        var device = new TestWaterConsumption(http);
        device.Initialize(Configuration(
            new() { ["securityToken"] = "token", ["endpoint"] = "https://water.invalid/api" },
            reports: [new ReportTemplate { Id = "water", Address = "watermeter", Parameters = new() { ["precision"] = "2" } }]));
        var reports = Capture(device);

        var start = device.StartAsync(CancellationToken.None);
        Assert.IsFalse(start.IsCompleted, "the start waits for the meter API without blocking the caller");
        var (request, _) = http.Requests.Single();
        Assert.AreEqual(("Bearer", "token"), (request.Headers.Authorization!.Scheme, request.Headers.Authorization.Parameter));
        StringAssert.StartsWith(request.RequestUri!.ToString(), "https://water.invalid/api?startDate=");

        http.Respond("""{"model":"m","serialNumber":"s","readings":[["2026-11-02T06:00:00",12.3456],["2026-11-02T05:00:00",12.0]]}""");
        await start;
        Assert.AreEqual(DeviceState.Running, device.State);
        Assert.AreEqual(12.35m, reports.Single().Value.GetValue<decimal>());

        using var cancel = new CancellationTokenSource();
        // A null report name is the device-level refresh the scheduler runs.
        var refresh = device.RefreshReportAsync(device.Id, null!, cancel.Token);
        Assert.IsFalse(refresh.IsCompleted);
        Assert.AreEqual(cancel.Token, http.Requests.Last().Token);
        await cancel.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => refresh);
        Assert.AreEqual(DeviceState.Running, device.State, "a cancelled refresh isn't an error");

        await device.StopAsync(CancellationToken.None);
        Assert.AreEqual(DeviceState.Stopped, device.State);
    }

    [TestMethod]
    public async Task ElectricityPriceStartIsCancellableWhileThePricesDownload()
    {
        var http = new FakeHttp();
        var device = new TestElectricityPrice(http);
        device.Initialize(Configuration(new() { ["endpoint"] = "https://prices.invalid/api", ["domain"] = "d", ["securityToken"] = "t", ["vat"] = "0" },
            reports: [new ReportTemplate { Id = "price", Address = "price" }]));
        var priceFile = Path.Combine("Data", "priceData.json");
        if (File.Exists(priceFile))
            Assert.Inconclusive($"{priceFile} exists in the test folder; the device would read it instead of downloading.");

        using var cancel = new CancellationTokenSource();
        var start = device.StartAsync(cancel.Token);
        Assert.IsFalse(start.IsCompleted);
        StringAssert.StartsWith(http.Requests.Single().Request.RequestUri!.ToString(), "https://prices.invalid/api?securityToken=t&documentType=A44");
        await cancel.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => start);
        Assert.AreEqual(DeviceState.Stopped, device.State);
    }

    [TestMethod]
    public async Task ApSystemsAwaitsTheMeterSummaryAndFailsOnAnErrorCode()
    {
        var client = new FakeApSystemsClient();
        var device = new ApSystems(NullLogger.Instance, client);
        device.Initialize(Configuration(new() { ["appId"] = "app", ["appSecret"] = "secret", ["sid"] = "sid", ["ecuId"] = "ecu" },
            reports: [new ReportTemplate { Id = "today", Address = "today" }, new ReportTemplate { Id = "year", Address = "year" }]));
        var reports = Capture(device);

        var start = device.StartAsync(CancellationToken.None);
        Assert.IsFalse(start.IsCompleted);
        Assert.AreEqual("ecu", client.EcuId);
        var meter = new EnergyMeter { Consumed = "1", Exported = "2", Imported = "3", Produced = "4" };
        client.Summary.SetResult(new MeterSummary { Code = ApCode.Success, Data = new Data { Today = meter, Year = meter, Month = meter, Lifetime = meter } });
        await start;
        CollectionAssert.AreEquivalent(new[] { "today", "year" }, reports.Select(r => r.Id).ToArray());

        client.Summary = new TaskCompletionSource<MeterSummary?>();
        client.Summary.SetResult(new MeterSummary { Code = ApCode.SystemBusy });
        await Assert.ThrowsAsync<Exception>(() => device.RefreshReportAsync(device.Id, null!, CancellationToken.None));
        Assert.AreEqual(DeviceState.Error, device.State);
    }

    // An HTTP timeout surfaces as TaskCanceledException without a cancelled token; it's an error with a reason.
    [TestMethod]
    public async Task ApSystemsTimeoutPutsTheDeviceIntoTheErrorState()
    {
        var client = new FakeApSystemsClient();
        client.Summary.SetException(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout."));
        var device = new ApSystems(NullLogger.Instance, client);
        device.Initialize(Configuration(new() { ["appId"] = "app", ["appSecret"] = "secret", ["sid"] = "sid", ["ecuId"] = "ecu" }));

        var error = await Assert.ThrowsAsync<Exception>(() => device.StartAsync(CancellationToken.None));
        Assert.IsInstanceOfType<TimeoutException>(error.InnerException);
        Assert.AreEqual(DeviceState.Error, device.State);
        StringAssert.Contains(device.StateMessage, "APsystems");
    }

    [TestMethod]
    public async Task HueAwaitsItsLightsForwardsCommandsAndStopsItsListener()
    {
        var device = new TestHue();
        device.Initialize(Configuration(new() { ["bridgeIpAddress"] = "hue.invalid", ["apiKey"] = "key" },
            reports: [new ReportTemplate { Id = "lamp", Address = "light-1" }],
            commands: [new CommandTemplate { Id = "lamp", Address = "light-1" }]));
        var reports = Capture(device);

        var start = device.StartAsync(CancellationToken.None);
        Assert.IsFalse(start.IsCompleted);
        Assert.IsTrue(device.ListenerToken.CanBeCanceled);
        device.Lights.SetResult("""{"data":[{"id":"light-1","metadata":{"name":"Lamp"},"on":{"on":true}}]}""".ToObj<HueLights>());
        await start;
        Assert.AreEqual("lamp", reports.Single().Id);
        Assert.AreEqual("Lamp", device.GetConfigurationTemplate().CommandTemplates!.Single().Name, "the template lists the lights read at start");

        using var cancel = new CancellationTokenSource();
        await device.ExecuteCommandAsync("lamp", """{"state":false}""", cancel.Token);
        Assert.AreEqual(("light-1", """{"state":false}""", cancel.Token), device.Commands.Single());

        await device.StopAsync(CancellationToken.None);
        Assert.IsTrue(device.ListenerToken.IsCancellationRequested);
        Assert.AreEqual(DeviceState.Stopped, device.State);
    }

    [TestMethod]
    public async Task MessagingSendsMailAsynchronouslyAndHonoursCancellation()
    {
        var device = new TestMessaging();
        device.Initialize(Configuration(new() { ["smtp_Server"] = "smtp.invalid", ["smtp_User"] = "riot2@example.com", ["smtp_Password"] = "pw" },
            commands: [new CommandTemplate { Id = "mail", Address = "mail" }]));
        await device.StartAsync(CancellationToken.None);

        using var cancel = new CancellationTokenSource();
        var send = device.ExecuteCommandAsync("mail", """{"to":"someone@example.com","subject":"s","body":"b"}""", cancel.Token);
        Assert.IsFalse(send.IsCompleted, "the command waits for the SMTP server without blocking");
        Assert.AreEqual("someone@example.com", device.Sent.Single().To.Single().Address);
        await cancel.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => send);
    }

    [TestMethod]
    public async Task AzureRelayAwaitsItsListenerAndStopsEvenWhenItNeverStarted()
    {
        var relay = new FakeRelay();
        var device = new AzureRelay(NullLogger.Instance, relay);
        device.Initialize(Configuration(new() { ["relayNamespace"] = "ns", ["connectionName"] = "c", ["keyName"] = "k", ["key"] = "secret" }));

        await device.StopAsync(CancellationToken.None);
        var start = device.StartAsync(CancellationToken.None);
        Assert.IsFalse(start.IsCompleted, "the start waits until the listener is open");
        relay.Open.SetResult();
        await start;
        await device.StopAsync(CancellationToken.None);

        Assert.AreEqual(("ns", "c", "k", "secret"), relay.Configured);
        Assert.AreEqual(2, relay.Stops);
    }

    [TestMethod]
    public async Task FtpKeepsItsServerRunningUntilStopAndReportsAServerThatCannotStart()
    {
        var ftp = new FakeFtp();
        // The memory storage isn't reached in this test.
        var device = new FTP(NullLogger.Instance, ftp, null!);
        device.Initialize(Configuration(new() { ["ftpUsers"] = "camera:pw", ["ftpPort"] = "2121" }));

        await device.StartAsync(CancellationToken.None);
        Assert.AreEqual((DeviceState.Running, 2121, "camera"), (device.State, ftp.Port, ftp.Users.Single().UserName));
        Assert.IsTrue(ftp.Subscribed);
        await device.StopAsync(CancellationToken.None);
        Assert.AreEqual(DeviceState.Stopped, device.State);
        Assert.IsFalse(ftp.Subscribed);

        ftp.StartError = new IOException("Address already in use");
        await Assert.ThrowsAsync<Exception>(() => device.StartAsync(CancellationToken.None));
        Assert.AreEqual(DeviceState.Error, device.State);
        Assert.IsFalse(ftp.Subscribed);
    }

    private static DeviceConfiguration Configuration(Dictionary<string, string> parameters,
        List<ReportTemplate>? reports = null, List<CommandTemplate>? commands = null) =>
        new() { Id = "device", Name = "Device", DeviceParameters = parameters, ReportTemplates = reports ?? [], CommandTemplates = commands ?? [] };

    private static ConcurrentQueue<Report> Capture(Core.Interfaces.IDevice device)
    {
        var reports = new ConcurrentQueue<Report>();
        device.ReportUpdated += (_, report) => reports.Enqueue((Report)report);
        return reports;
    }

    private static TaskCompletionSource<T> Pending<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    // One pending response at a time; a send waits for Respond or for its token.
    private sealed class FakeHttp
    {
        private TaskCompletionSource<HttpResponseMessage> _next = Pending<HttpResponseMessage>();

        public ConcurrentQueue<(HttpRequestMessage Request, CancellationToken Token)> Requests { get; } = new();

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue((request, cancellationToken));
            return _next.Task.WaitAsync(cancellationToken);
        }

        public void Respond(string body)
        {
            var current = _next;
            _next = Pending<HttpResponseMessage>();
            current.SetResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    private sealed class TestWaterConsumption(FakeHttp http) : WaterConsumption(NullLogger.Instance)
    {
        internal override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            http.SendAsync(request, cancellationToken);
    }

    private sealed class TestElectricityPrice(FakeHttp http) : ElectricityPrice(NullLogger.Instance)
    {
        internal override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            http.SendAsync(request, cancellationToken);
    }

    private sealed class TestHue() : Hue(NullLogger.Instance)
    {
        public TaskCompletionSource<HueLights?> Lights { get; } = Pending<HueLights?>();
        public ConcurrentQueue<(string Light, string Command, CancellationToken Token)> Commands { get; } = new();
        public CancellationToken ListenerToken { get; private set; }

        internal override Task<HueLights?> GetLightsAsync(CancellationToken cancellationToken) => Lights.Task.WaitAsync(cancellationToken);

        internal override Task SetLightAsync(string hueId, string commandJson, CancellationToken cancellationToken)
        {
            Commands.Enqueue((hueId, commandJson, cancellationToken));
            return Task.CompletedTask;
        }

        internal override Task StartEventsListenerAsync(CancellationToken cancellationToken)
        {
            ListenerToken = cancellationToken;
            return Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }

    private sealed class TestMessaging() : Messaging(NullLogger.Instance)
    {
        public ConcurrentQueue<MailMessage> Sent { get; } = new();

        internal override Task SendMailAsync(SmtpClient client, MailMessage message, CancellationToken cancellationToken)
        {
            Sent.Enqueue(message);
            return Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }

    private sealed class FakeApSystemsClient : IApSystemsClientService
    {
        public TaskCompletionSource<MeterSummary?> Summary { get; set; } = Pending<MeterSummary?>();
        public string? EcuId { get; private set; }

        public void Configure(string? appId, string? appSecret, string? sid) { }

        public Task<MeterSummary?> GetMeterSummaryAsync(string? ecuid, CancellationToken cancellationToken = default)
        {
            EcuId = ecuid;
            return Summary.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class FakeRelay : IAzureRelayService
    {
        public event WebMessageHandler? MessageReceived { add { } remove { } }
        public TaskCompletionSource Open { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public (string, string, string, string) Configured { get; private set; }
        public int Stops { get; private set; }

        public void Configure(string relayNamespace, string connectionName, string keyName, string key) =>
            Configured = (relayNamespace, connectionName, keyName, key);

        public Task StartAsync() => Open.Task;

        public Task StopAsync()
        {
            Stops++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeFtp : IFtpService
    {
        private TaskCompletionSource? _running;
        private CustomLocalDataConnection.FileReceivedHandler? _handlers;

        public event CustomLocalDataConnection.FileReceivedHandler? FileReceived { add => _handlers += value; remove => _handlers -= value; }
        public bool Subscribed => _handlers != null;
        public Exception? StartError { get; set; }
        public int Port { get; private set; }
        // StartAsync sets it.
        public List<FtpUser> Users { get; private set; } = null!;

        public Task StartAsync(List<FtpUser> users, int port)
        {
            (Users, Port) = (users, port);
            if (StartError != null)
                return Task.FromException(StartError);
            _running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _running.Task;
        }

        public void Stop() => _running?.TrySetCanceled();
    }
}
