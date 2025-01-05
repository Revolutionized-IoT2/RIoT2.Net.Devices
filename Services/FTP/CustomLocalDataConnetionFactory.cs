using System.Net;
using static RIoT2.Net.Devices.Services.FTP.CustomLocalDataConnection;
using Zhaobang.FtpServer.Connections;

namespace RIoT2.Net.Devices.Services.FTP
{
    internal class CustomLocalDataConnectionFactory : IDataConnectionFactory
    {
        private readonly FileReceivedHandler _fileReceivedReadyHandler;

        public CustomLocalDataConnectionFactory(FileReceivedHandler fileReceivedHandler)
        {
            _fileReceivedReadyHandler = fileReceivedHandler;
        }

        public IDataConnection GetDataConnection(IPAddress localIP)
        {
            var customLocalDataConnection = new CustomLocalDataConnection(localIP);
            customLocalDataConnection.FileReceivedEvent += _fileReceivedReadyHandler;
            return customLocalDataConnection;
        }
    }
}
