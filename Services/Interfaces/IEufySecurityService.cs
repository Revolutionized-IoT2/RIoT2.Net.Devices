using RIoT2.Net.Devices.Models;

namespace RIoT2.Net.Devices.Services.Interfaces
{
    public delegate void EufyEventHandler(EufyEventMessage data);

    public interface IEufySecurityService
    {
        EufyPropertiesData StationProperties { get; }
        Dictionary<string, EufyPropertiesData> DeviceProperties { get; }
        void Configure(string serviceIp, int port);
        event EufyEventHandler EufyEvent;
        /// <summary>Connects to eufy-security-ws. A failed connection is logged, not thrown.</summary>
        Task StartAsync(CancellationToken cancellationToken);
        /// <summary>Disconnects and waits until the received messages are handled.</summary>
        Task StopAsync();
        Task SendCommand(string json);
    }
}