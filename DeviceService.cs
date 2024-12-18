using Microsoft.Extensions.Logging;
using RIoT2.Core.Abstracts;
using RIoT2.Core.Interfaces;
using RIoT2.Core.Interfaces.Services;
using RIoT2.Net.Devices.Catalog;

namespace RIoT2.Net.Devices
{
    public class DeviceService : DeviceServiceBase, IDeviceService
    {
        public DeviceService(
            INodeConfigurationService configurationService,
            ILogger<DeviceService> logger,
            IWebhookService webhookService,
            IFtpService ftpService,
            IStorageService storageService,
            IDownloadService downloadService,
            IMemoryStorageService memoryStorageService,
            IAzureRelayService azureRelayService
                ) : base(configurationService, logger,
                    new List<IDevice>() {
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
                    })
        { }
    }
}
