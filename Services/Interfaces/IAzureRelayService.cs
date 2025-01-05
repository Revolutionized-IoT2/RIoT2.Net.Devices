using RIoT2.Core;

namespace RIoT2.Net.Devices.Services.Interfaces
{
    public interface IAzureRelayService
    {
        event WebMessageHandler MessageReceived;
        Task StartAsync();
        Task StopAsync();
        void Configure(string relayNamespace, string connectionName, string keyName, string key);
    }
}