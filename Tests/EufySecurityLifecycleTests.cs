using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RIoT2.Core.Models;
using RIoT2.Net.Devices.Catalog;
using RIoT2.Net.Devices.Models;
using RIoT2.Net.Devices.Services;
using RIoT2.Net.Devices.Services.Interfaces;

namespace RIoT2.Net.Devices.Tests;

/// <summary>The Eufy Security service and device without blocking calls or async void (plan M11).</summary>
[TestClass]
public class EufySecurityLifecycleTests
{
    private const string FailingMessage = """{"type":"result","success":true,"messageId":"start_listening","result":{}}""";
    private const string DeviceMessage = """{"type":"result","success":true,"messageId":"device.get_properties","result":{"serialNumber":"T1","properties":{"name":"Door"}}}""";
    private const string StationMessage = """{"type":"result","success":true,"messageId":"station.get_properties","result":{"properties":{"serialNumber":"S1","guardMode":1}}}""";

    [TestMethod]
    public async Task MessagesAreHandledInOrderAndAFailingMessageDoesNotStopTheNext()
    {
        var logger = new ListLogger<EufySecurityService>();
        await using var service = new EufySecurityService(logger);
        service.Configure("127.0.0.1", ClosedPort());

        await service.StartAsync(CancellationToken.None);
        var connectErrors = logger.Errors.Count;
        service.EnqueueMessage(FailingMessage);
        service.EnqueueMessage(DeviceMessage);
        service.EnqueueMessage(StationMessage);
        await service.StopAsync();

        Assert.AreEqual(1, connectErrors, "The unreachable service is logged, not thrown.");
        Assert.AreEqual(2, logger.Errors.Count);
        Assert.AreEqual("Door", service.DeviceProperties["T1"].Name);
        Assert.AreEqual("S1", service.StationProperties.SerialNumber);
    }

    [TestMethod]
    public async Task ServiceHandlesMessagesAgainAfterARestart()
    {
        await using var service = new EufySecurityService(NullLogger<EufySecurityService>.Instance);
        service.Configure("127.0.0.1", ClosedPort());
        await service.StartAsync(CancellationToken.None);
        await service.StopAsync();
        service.EnqueueMessage(DeviceMessage);

        // The first stop used to cancel the service for good.
        await service.StartAsync(CancellationToken.None);
        service.EnqueueMessage(StationMessage);
        await service.StopAsync();

        Assert.IsFalse(service.DeviceProperties.ContainsKey("T1"), "A message after stop is ignored.");
        Assert.AreEqual("S1", service.StationProperties?.SerialNumber);
    }

    [TestMethod]
    public async Task DeviceAwaitsTheServiceSendsInitialReportsAndCommands()
    {
        var service = new FakeEufyService();
        var device = new EufySecurity(NullLogger.Instance, service, null, TimeSpan.Zero);
        device.Initialize(device.GetConfigurationTemplate());
        var reports = new List<Report>();
        device.ReportUpdated += (_, report) => reports.Add((Report)report);

        await device.StartAsync(CancellationToken.None);
        await device.ExecuteCommandAsync(device.CommandTemplates.Single().Id, "2", CancellationToken.None);
        await device.StopAsync(CancellationToken.None);

        Assert.AreEqual(1, service.Starts);
        Assert.AreEqual(1, service.Stops);
        Assert.IsFalse(service.HasSubscribers);
        Assert.AreEqual(1, reports.Count);
        Assert.AreEqual(new ValueModel(1).ToJson(), reports[0].Value.ToJson());
        Assert.AreEqual(1, service.Sent.Count);
        StringAssert.Contains(service.Sent[0], "guardMode");
    }

    [TestMethod]
    public async Task DeviceStartsWhenTheStationHasNotReportedYet()
    {
        var service = new FakeEufyService { Station = null };
        var device = new EufySecurity(NullLogger.Instance, service, null, TimeSpan.Zero);
        device.Initialize(device.GetConfigurationTemplate());

        await device.StartAsync(CancellationToken.None);

        Assert.AreEqual(RIoT2.Core.DeviceState.Running, device.State);
        await device.StopAsync(CancellationToken.None);
    }

    private static int ClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        private readonly ConcurrentQueue<Exception> _errors = new();
        public IReadOnlyCollection<Exception> Errors => _errors.ToArray();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            if (logLevel >= LogLevel.Error)
                _errors.Enqueue(exception);
        }
    }

    private sealed class FakeEufyService : IEufySecurityService
    {
        public int Starts;
        public int Stops;
        public List<string> Sent { get; } = [];
        public EufyPropertiesData Station { get; set; } = new() { SerialNumber = "S1", GuardMode = 1 };
        public EufyPropertiesData StationProperties => Station;
        public Dictionary<string, EufyPropertiesData> DeviceProperties { get; } = [];
        public bool HasSubscribers => EufyEvent != null;
        public event EufyEventHandler EufyEvent;
        public void Configure(string serviceIp, int port) { }
        public Task StartAsync(CancellationToken cancellationToken) { Starts++; return Task.CompletedTask; }
        public Task StopAsync() { Stops++; return Task.CompletedTask; }
        public Task SendCommand(string json) { Sent.Add(json); return Task.CompletedTask; }
    }
}
