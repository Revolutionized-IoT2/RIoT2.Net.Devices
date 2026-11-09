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
            AddService<IEufySdkService, Services.EufySdk.EufySdkService>();
            AddService<INetatmoAuthClientFactory, NetatmoAuthClientFactory>();
            AddService<INetatmoWebhookHub, Services.Netatmo.NetatmoWebhookHub>();
            AddService<ICastAudioCache, Services.GoogleCast.CastAudioCache>();

            AddDevice<Web>();
            AddDevice<Catalog.Timer>();
            AddDevice<Virtual>();
            AddDevice<Mqtt>();
            AddDevice<WaterConsumption>();
            AddDevice<Messaging>();
            AddDevice<FTP>();
            AddDevice<ElectricityPrice>();
            AddDevice<WeatherForecast>();
            AddDevice<EasyPLC>();
            AddDevice<NetatmoWeather>();
            AddDevice<NetatmoSecurity>();
            AddDevice<Hue>();
            AddDevice<AzureRelay>();
            AddDevice<ApSystems>();
            AddDevice<EufySecurity>();
            AddDevice<EufySecuritySdk>();
            AddDevice<MelCloud>();
            AddDevice<MyUplink>();
            AddDevice<Themo>();
            AddDevice<Miele>();
            AddDevice<Harvia>();
            AddDevice<DefaPower>();
            AddDevice<GoogleCast>();
            AddDevice<Govee>();
        }
    }
}