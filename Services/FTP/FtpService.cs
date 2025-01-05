using System.Net;
using Zhaobang.FtpServer;
using static RIoT2.Net.Devices.Services.FTP.CustomLocalDataConnection;
using RIoT2.Net.Devices.Services.Interfaces;

namespace RIoT2.Net.Devices.Services.FTP
{
    public class FtpService : IFtpService
    {
        private FtpServer _ftpServer;
        private CancellationToken _token;
        private CancellationTokenSource _tokenSource;

        public event FileReceivedHandler FileReceived;

        private void fileReceived(InMemoryStream inMemoryStream)
        {
            FileReceived(inMemoryStream);
        }

        public void Stop()
        {
            if (_token.CanBeCanceled)
                _tokenSource.Cancel();

            _tokenSource = null;
        }

        public async Task StartAsync(List<FtpUser> users, int port)
        {
            _ftpServer = new FtpServer(
                new IPEndPoint(IPAddress.Any, port),
                new InMemoryFileProviderFactory(),
                new CustomLocalDataConnectionFactory(fileReceived),
                new Authenticator(users)
            );

            _tokenSource = new CancellationTokenSource();
            _token = _tokenSource.Token;
            await _ftpServer.RunAsync(_token);
        }
    }
}