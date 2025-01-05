using System.Net;
using Zhaobang.FtpServer.Connections;

namespace RIoT2.Net.Devices.Services.FTP
{
    public class CustomLocalDataConnection : IDataConnection
    {
        public delegate void FileReceivedHandler(InMemoryStream inMemoryStream);
        public event FileReceivedHandler FileReceivedEvent;

        private LocalDataConnection _localDataConnection;

        public CustomLocalDataConnection(IPAddress localIP)
        {
            _localDataConnection = new LocalDataConnection(localIP);
        }

        public bool IsOpen => _localDataConnection.IsOpen;

        public IEnumerable<int> SupportedActiveProtocal => _localDataConnection.SupportedActiveProtocal;

        public IEnumerable<int> SupportedPassiveProtocal => _localDataConnection.SupportedPassiveProtocal;

        public Task AcceptAsync()
        {
            return _localDataConnection.AcceptAsync();
        }

        public void Close()
        {
            _localDataConnection.Close();
        }

        public Task ConnectActiveAsync(IPAddress remoteIP, int remotePort, int protocol)
        {
            return _localDataConnection.ConnectActiveAsync(remoteIP, remotePort, protocol);
        }

        public Task DisconnectAsync()
        {
            return _localDataConnection.DisconnectAsync();
        }

        public int ExtendedListen(int protocol)
        {
            return _localDataConnection.ExtendedListen(protocol);
        }

        public IPEndPoint Listen()
        {
            return _localDataConnection.Listen();
        }

        public Task RecieveAsync(Stream streamToWrite)
        {
            _localDataConnection.RecieveAsync(streamToWrite).Wait();
            FileReceivedEvent((InMemoryStream)streamToWrite);

            return Task.CompletedTask;
        }

        public Task SendAsync(Stream streamToRead)
        {
            return _localDataConnection.SendAsync(streamToRead);
        }
    }
}
