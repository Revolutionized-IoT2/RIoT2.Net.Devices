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
        void Start();
        void Stop();
        Task SendCommand(string json);
    }
}