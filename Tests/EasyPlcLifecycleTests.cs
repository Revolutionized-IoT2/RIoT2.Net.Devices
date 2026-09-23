using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RIoT2.Core.Models;
using RIoT2.Net.Devices.Catalog;
using RIoT2.Net.Devices.Services;

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

    private static DeviceConfiguration Configuration(string host = "plc.invalid", int port = 10001) => new()
    {
        Id = "plc",
        DeviceParameters = new() { ["ipAddress"] = host, ["port"] = port.ToString() },
        ReportTemplates = [],
        CommandTemplates = []
    };

    private sealed class FakeConnection(string failure = null) : IEasyPlcConnection
    {
        public bool Connected { get; private set; }
        public bool Disposed { get; private set; }
        public int DisposeCount { get; private set; }
        public (string, int) Endpoint { get; private set; }
        public DuplexStream Stream { get; } = new(failure);

        public Task ConnectAsync(string host, int port)
        {
            Assert.IsFalse(Disposed);
            Endpoint = (host, port);
            if (failure == "connect")
                throw new IOException("Synthetic connection failure.");
            Connected = true;
            return Task.CompletedTask;
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

        public DuplexStream(string failure)
        {
            byte[] success = [0x65, 0x06, 0x00, 0x00, 0x00, 0x41, 0x48, 0x30];
            byte[] responses = failure switch
            {
                "truncated" => [0x65],
                "rejected" => new byte[16],
                _ => [.. success, .. success]
            };
            _responses = new MemoryStream(responses);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadCalls++;
            return ValueTask.FromResult(_responses.Read(buffer.Span[..Math.Min(buffer.Length, MaxRead)]));
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
