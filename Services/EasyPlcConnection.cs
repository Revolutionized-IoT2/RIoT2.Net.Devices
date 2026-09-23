using System.Net.Sockets;

namespace RIoT2.Net.Devices.Services
{
    internal interface IEasyPlcConnection : IDisposable
    {
        bool Connected { get; }
        Task ConnectAsync(string host, int port, CancellationToken cancellationToken);
        Stream GetStream();
    }

    internal sealed class EasyPlcConnection : IEasyPlcConnection
    {
        private readonly TcpClient _client = new();
        public bool Connected => _client.Connected;
        public Task ConnectAsync(string host, int port, CancellationToken cancellationToken) =>
            _client.ConnectAsync(host, port, cancellationToken).AsTask();
        public Stream GetStream() => _client.GetStream();
        public void Dispose() => _client.Dispose();
    }
}
