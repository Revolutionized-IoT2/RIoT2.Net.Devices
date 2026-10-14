using RIoT2.Net.Devices.Services;

namespace RIoT2.Net.Devices.Services.Interfaces
{
    /// <summary>One <see cref="NetatmoAuthClient"/> per Netatmo app (client id), shared by the devices using it.</summary>
    public interface INetatmoAuthClientFactory
    {
        NetatmoAuthClient Get(string clientId);
    }
}
