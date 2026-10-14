using RIoT2.Core.Abstracts;
using RIoT2.Net.Devices.Catalog;
using RIoT2.Net.Devices.Services;
using RIoT2.Net.Devices.Services.FTP;
using RIoT2.Net.Devices.Services.Interfaces;

namespace RIoT2.Net.Devices
{
    public class Plugin : PluginBase
    {
        protected override void Register()
        {
            AddService<IWebhookService, WebhookService>();
            AddService<IFtpService, FtpService>();
            AddService<IStorageService, FTPStorageService>();
            AddService<IAzureRelayService, AzureRelayService>();
            AddService<IMemoryStorageService, MemoryStorageService>();
            AddService<IApSystemsClientService, ApSystemsClientService>();
            AddService<IEufySecurityService, EufySecurityService>();
            AddService<INetatmoAuthClientFactory, NetatmoAuthClientFactory>();

            AddDevice<Web>();
            AddDevice<Catalog.Timer>();
            AddDevice<Virtual>();
            AddDevice<Mqtt>();
            AddDevice<WaterConsumption>();
            AddDevice<Messaging>();
            AddDevice<FTP>();
            AddDevice<ElectricityPrice>();
            AddDevice<EasyPLC>();
            AddDevice<NetatmoWeather>();
            AddDevice<NetatmoSecurity>();
            AddDevice<Hue>();
            AddDevice<AzureRelay>();
            AddDevice<ApSystems>();
            AddDevice<EufySecurity>();
        }
    }
}