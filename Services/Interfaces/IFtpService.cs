using static RIoT2.Net.Devices.Services.FTP.CustomLocalDataConnection;
using RIoT2.Net.Devices.Services.FTP;

namespace RIoT2.Net.Devices.Services.Interfaces
{
    public interface IFtpService
    {
        event FileReceivedHandler FileReceived;
        Task StartAsync(List<FtpUser> users, int port);
        void Stop();
    }
}