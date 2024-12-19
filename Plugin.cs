using Microsoft.Extensions.Logging;
using RIoT2.Core.Interfaces;
using RIoT2.Core.Interfaces.Services;
using RIoT2.Net.Devices.Catalog;

namespace RIoT2.Net.Devices
{
    public class Plugin : IDevicePlugin
    {
        private List<IDevice> _devices;

        public Plugin(
            INodeConfigurationService configurationService,
            ILogger<Plugin> logger,
            IWebhookService webhookService,
            IFtpService ftpService,
            IStorageService storageService,
            IDownloadService downloadService,
            IMemoryStorageService memoryStorageService,
            IAzureRelayService azureRelayService) {
            _devices = [
                //new Web(logger, webhookService),
                //new Catalog.Timer(logger),
                //new Virtual(logger),
                //new Mqtt(logger),
                new WaterConsumption(logger),
                new Messaging(logger),
                new FTP(logger, ftpService, downloadService, memoryStorageService),
                new ElectricityPrice(logger),
                new EasyPLC(logger),
                //new NetatmoWeather(logger),
                new NetatmoSecurity(logger, downloadService, memoryStorageService),
                new Hue(logger),
                new AzureRelay(logger, azureRelayService)
            ];
        }

        public List<IDevice> Devices 
        {
            get { return _devices; }
        }
    }
}
