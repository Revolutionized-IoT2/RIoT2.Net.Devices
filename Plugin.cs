using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RIoT2.Core.Interfaces;
using RIoT2.Core.Interfaces.Services;
using RIoT2.Net.Devices.Catalog;

namespace RIoT2.Net.Devices
{
    public class Plugin : IDevicePlugin
    {
        private List<IDevice> _devices;

        public Plugin(IServiceProvider services)
        {
            INodeConfigurationService configurationService = services.GetRequiredService<INodeConfigurationService>();
            ILogger logger = services.GetRequiredService<ILogger>();
            IWebhookService webhookService = services.GetRequiredService<IWebhookService>();
            IFtpService ftpService = services.GetRequiredService<IFtpService>();
            IStorageService storageService = services.GetRequiredService<IStorageService>();
            IDownloadService downloadService = services.GetRequiredService<IDownloadService>();
            IMemoryStorageService memoryStorageService = services.GetRequiredService<IMemoryStorageService>();
            IAzureRelayService azureRelayService = services.GetRequiredService<IAzureRelayService>();

            //Test that all services are ok!
            if (configurationService == null ||
                logger == null ||
                webhookService == null ||
                ftpService == null ||
                storageService == null ||
                downloadService == null ||
                memoryStorageService == null ||
                azureRelayService == null
                ) {
                throw new Exception($"At Least one required Service is not setup properly. Could not start plugin. " +
                    $"configurationService({configurationService != null}) " +
                    $"logger({logger != null}) " +
                    $"webhookService({webhookService != null}) " +
                    $"ftpService({ftpService != null}) " +
                    $"storageService({storageService != null}) " +
                    $"downloadService({downloadService != null}) " +
                    $"memoryStorageService({memoryStorageService != null}) " +
                    $"azureRelayService({azureRelayService != null})");
            }

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