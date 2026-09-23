using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RIoT2.Core.Models;
using RIoT2.Net.Devices.Catalog;
using RIoT2.Net.Devices.Services;
using RIoT2.Core;

namespace RIoT2.Net.Devices.Tests;

[TestClass]
public class EasyPlcLifecycleTests
{
    [TestMethod]
    public void ReconfigurationCreatesFreshConnectionAndUsesNewEndpoint()
    {
        var first = new FakeConnection();
        var second = new FakeConnection();
        var connections = new Queue<IEasyPlcConnection>([first, second]);
        var device = new EasyPLC(NullLogger.Instance, connections.Dequeue);
        device.Initialize(Configuration("first.invalid", 10001));
        device.Start();
        device.Stop();
        device.Initialize(Configuration("second.invalid", 10002));
        device.Start();
        device.Stop();

        Assert.AreEqual(("first.invalid", 10001), first.Endpoint);
        Assert.AreEqual(("second.invalid", 10002), second.Endpoint);
        Assert.IsTrue(first.Disposed);
        Assert.IsTrue(second.Disposed);
        byte[] expectedHandshake =
        [
            0x45, 0x07, 0x00, 0x00, 0x80, 0x00, 0x00, 0x00, 0x5f,
            0x45, 0x07, 0x01, 0x00, 0x80, 0x00, 0x00, 0x3d, 0x9f
        ];
        CollectionAssert.AreEqual(expectedHandshake, first.Stream.Written);
        CollectionAssert.AreEqual(expectedHandshake, second.Stream.Written);
        Assert.AreEqual(0, connections.Count);
    }

    [TestMethod]
    public void RepeatedStartAndStopAreIdempotent()
    {
        var connection = new FakeConnection();
        var created = 0;
        var device = new EasyPLC(NullLogger.Instance, () => { created++; return connection; });
        device.Initialize(Configuration());

        device.Start();
        device.Start();
        device.Stop();
        device.Stop();

        Assert.AreEqual(1, created);
        Assert.AreEqual(1, connection.DisposeCount);
    }

    [DataTestMethod]
    [DataRow("connect")]
    [DataRow("truncated")]
    [DataRow("rejected")]
    public void FailedConnectionIsDisposedAndNextStartCanRetry(string failure)
    {
        var failed = new FakeConnection(failure);
        var successful = new FakeConnection();
        var connections = new Queue<IEasyPlcConnection>([failed, successful]);
        var device = new EasyPLC(NullLogger.Instance, connections.Dequeue);
        device.Initialize(Configuration());

        Assert.ThrowsException<Exception>(device.Start);
        Assert.IsTrue(failed.Disposed);
        device.Start();
        device.Stop();

        Assert.IsTrue(successful.Disposed);
        Assert.AreEqual(0, connections.Count);
    }

    [TestMethod]
    public void FragmentedInitializationResponsesAreReadCompletely()
    {
        var connection = new FakeConnection();
        connection.Stream.MaxRead = 1;
        var device = new EasyPLC(NullLogger.Instance, () => connection);
        device.Initialize(Configuration());

        device.Start();
        device.Stop();

        Assert.AreEqual(16, connection.Stream.ReadCalls);
    }

    [TestMethod]
    public async Task FragmentedCommandResponseUsesCompleteFrameAndExactWriteBytes()
    {
        var connection = new FakeConnection(tail: Ack);
        connection.Stream.MaxRead = 1;
        var device = new EasyPLC(NullLogger.Instance, () => connection);
        device.Initialize(CommandConfiguration());
        await device.StartAsync(default);
        await device.ExecuteCommandAsync("set", "true", default);
        byte[] content = [0x08, 0x21, 0x00, 0x04, 0x00, 0x00, 0x01];
        byte[] expected = [0x45, .. content, .. Crc(content)];
        CollectionAssert.AreEqual(expected, connection.Stream.Written.Skip(18).ToArray());
        await device.StopAsync(default);
    }

    [TestMethod]
    public async Task DisconnectedCommandDisposesSocketAndNextCommandReconnects()
    {
        var broken = new FakeConnection(tail: [0x65]);
        var replacement = new FakeConnection(tail: Ack);
        var connections = new Queue<IEasyPlcConnection>([broken, replacement]);
        var device = new EasyPLC(NullLogger.Instance, connections.Dequeue);
        device.Initialize(CommandConfiguration());
        await device.StartAsync(default);
        await Assert.ThrowsExceptionAsync<EndOfStreamException>(() => device.ExecuteCommandAsync("set", "true", default));
        Assert.IsTrue(broken.Disposed);
        await device.ExecuteCommandAsync("set", "false", default);
        await device.StopAsync(default);
        Assert.IsTrue(replacement.Disposed);
    }

    [TestMethod]
    public async Task StopCancelsInFlightRefreshWithoutPublishingPartialData()
    {
        var connection = new FakeConnection(stall: true);
        var device = new EasyPLC(NullLogger.Instance, () => connection);
        var configuration = Configuration();
        configuration.ReportTemplates = [new ReportTemplate { Id = "marker", Address = "M-1-0-1" }];
        device.Initialize(configuration);
        var reports = 0;
        device.ReportUpdated += (_, _) => reports++;
        await device.StartAsync(default);
        var refreshing = device.RefreshReportAsync(device.Id, device.Id, default);
        await connection.Stream.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await device.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(3));
        await ExpectCancellation(refreshing);
        Assert.AreEqual(0, reports);
        Assert.AreEqual(DeviceState.Stopped, device.State);
        Assert.AreEqual(1, connection.DisposeCount);
    }

    [TestMethod]
    public async Task ConnectCanBeCancelledAndRestartedWithFreshSocket()
    {
        var stalled = new FakeConnection("stalled-connect");
        var replacement = new FakeConnection();
        var connections = new Queue<IEasyPlcConnection>([stalled, replacement]);
        var device = new EasyPLC(NullLogger.Instance, connections.Dequeue);
        device.Initialize(Configuration());
        using var cancellation = new CancellationTokenSource();
        var starting = device.StartAsync(cancellation.Token);
        await stalled.Connecting.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        await ExpectCancellation(starting);
        Assert.IsTrue(stalled.Disposed);
        await device.StartAsync(default);
        await device.StopAsync(default);
        Assert.IsTrue(replacement.Disposed);
    }

    [TestMethod]
    public async Task StalledCommandHasFiniteDeadlineAndDisposesConnection()
    {
        var connection = new FakeConnection(stall: true);
        var device = new EasyPLC(NullLogger.Instance, () => connection);
        device.Initialize(CommandConfiguration());
        await device.StartAsync(default);
        await Assert.ThrowsExceptionAsync<TimeoutException>(() =>
            device.ExecuteCommandAsync("set", "true", default)).WaitAsync(TimeSpan.FromSeconds(8));
        Assert.IsTrue(connection.Disposed);
        await device.StopAsync(default);
    }

    [TestMethod]
    public async Task MarkerRefreshReportsOnlyAfterCompleteValidatedFrame()
    {
        var connection = new FakeConnection(tail: Ack);
        connection.Stream.MaxRead = 1;
        var device = new EasyPLC(NullLogger.Instance, () => connection);
        var configuration = Configuration();
        configuration.ReportTemplates = [new ReportTemplate { Id = "marker", Address = "M-1-0-1" }];
        device.Initialize(configuration);
        Report received = null;
        device.ReportUpdated += (_, report) => received = (Report)report;
        await device.StartAsync(default);
        await device.RefreshReportAsync(device.Id, device.Id, default);
        Assert.IsNotNull(received);
        Assert.AreEqual("marker", received.Id);
        Assert.IsFalse(received.Value.GetValue<bool>());
        await device.StopAsync(default);
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task MalformedResponsesFailAndResetTransport(bool badHeader)
    {
        var response = Ack.ToArray();
        if (badHeader) response[0] = 0;
        else response[^1] ^= 0xff;
        var connection = new FakeConnection(tail: response);
        var device = new EasyPLC(NullLogger.Instance, () => connection);
        device.Initialize(CommandConfiguration());
        await device.StartAsync(default);
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => device.ExecuteCommandAsync("set", "true", default));
        Assert.IsTrue(connection.Disposed);
        await device.StopAsync(default);
    }

    private static readonly byte[] Ack = [0x65, 0x05, 0, 0, 0, 0, 0xcc];
    private static DeviceConfiguration CommandConfiguration()
    {
        var configuration = Configuration();
        configuration.CommandTemplates = [new CommandTemplate { Id = "set", Address = "M-1-0-1" }];
        return configuration;
    }
    private static byte[] Crc(byte[] bytes)
    {
        ushort crc = 0;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (ushort)((crc & 1) != 0 ? (crc >> 1) ^ 0xa001 : crc >> 1);
        }
        return [(byte)crc, (byte)(crc >> 8)];
    }
    private static async Task ExpectCancellation(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(3)); Assert.Fail("Expected cancellation."); }
        catch (OperationCanceledException) { }
    }

    private static DeviceConfiguration Configuration(string host = "plc.invalid", int port = 10001) => new()
    {
        Id = "plc",
        DeviceParameters = new() { ["ipAddress"] = host, ["port"] = port.ToString() },
        ReportTemplates = [],
        CommandTemplates = []
    };

    private sealed class FakeConnection(string failure = null, byte[] tail = null, bool stall = false) : IEasyPlcConnection
    {
        public bool Connected { get; private set; }
        public bool Disposed { get; private set; }
        public int DisposeCount { get; private set; }
        public (string, int) Endpoint { get; private set; }
        public DuplexStream Stream { get; } = new(failure, tail, stall);
        public TaskCompletionSource Connecting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ConnectAsync(string host, int port, CancellationToken cancellationToken)
        {
            Assert.IsFalse(Disposed);
            Endpoint = (host, port);
            Connecting.TrySetResult();
            if (failure == "stalled-connect")
                await Task.Delay(Timeout.Infinite, cancellationToken);
            if (failure == "connect")
                throw new IOException("Synthetic connection failure.");
            Connected = true;
        }

        public Stream GetStream()
        {
            Assert.IsFalse(Disposed);
            return Stream;
        }

        public void Dispose()
        {
            DisposeCount++;
            Disposed = true;
            Connected = false;
            Stream.Dispose();
        }
    }

    private sealed class DuplexStream : Stream
    {
        private readonly MemoryStream _responses;
        public List<byte> Written { get; } = [];
        public int MaxRead { get; set; } = int.MaxValue;
        public int ReadCalls { get; private set; }
        private readonly bool _stall;
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public DuplexStream(string failure, byte[] tail, bool stall)
        {
            _stall = stall;
            byte[] success = [0x65, 0x06, 0x00, 0x00, 0x00, 0x41, 0x48, 0x30];
            byte[] responses = failure switch
            {
                "truncated" => [0x65],
                "rejected" => new byte[16],
                _ => [.. success, .. success, .. tail ?? []]
            };
            _responses = new MemoryStream(responses);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadCalls++;
            if (_stall && _responses.Position == _responses.Length)
            {
                Waiting.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            return _responses.Read(buffer.Span[..Math.Min(buffer.Length, MaxRead)]);
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            Written.AddRange(buffer.AsSpan(offset, count).ToArray());
        public override int Read(byte[] buffer, int offset, int count) =>
            _responses.Read(buffer, offset, Math.Min(count, MaxRead));
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _responses.Dispose();
            base.Dispose(disposing);
        }
    }
}
