using RIoT2.Net.Devices.Catalog;
using Microsoft.Extensions.DependencyInjection;
using RIoT2.Net.Devices.Services.Interfaces;
using RIoT2.Net.Devices.Services;
using RIoT2.Net.Devices.Services.FTP;

namespace RIoT2.Net.Devices
{
    public class Plugin : Core.Interfaces.IDevicePlugin
    {
        private List<Core.Interfaces.IDevice> _devices;
        public Plugin(){ }

        public List<Core.Interfaces.IDevice> Devices
        {
            get { return _devices; }
        }

        public void Initialize(IServiceCollection services) 
        {
            //Initialize required device services
            services.AddSingleton<IWebhookService, WebhookService>();
            services.AddSingleton<IFtpService, FtpService>();
            services.AddSingleton<IStorageService, FTPStorageService>();
            services.AddSingleton<IDownloadService, DownloadService>();
            services.AddSingleton<IAzureRelayService, AzureRelayService>();
            services.AddSingleton<IMemoryStorageService, MemoryStorageService>();

            //Initialize devices and add them to list
            var serviceProvider = services.BuildServiceProvider();
            _devices = [
               ActivatorUtilities.CreateInstance<Web>(serviceProvider),
               ActivatorUtilities.CreateInstance<Catalog.Timer>(serviceProvider),
               ActivatorUtilities.CreateInstance<Virtual>(serviceProvider),
               ActivatorUtilities.CreateInstance<Mqtt>(serviceProvider),
               ActivatorUtilities.CreateInstance<WaterConsumption>(serviceProvider),
               ActivatorUtilities.CreateInstance<Messaging>(serviceProvider),
               ActivatorUtilities.CreateInstance<FTP>(serviceProvider), //memorystorage
               ActivatorUtilities.CreateInstance<ElectricityPrice>(serviceProvider),
               ActivatorUtilities.CreateInstance<EasyPLC>(serviceProvider),
               ActivatorUtilities.CreateInstance<NetatmoWeather>(serviceProvider),
               ActivatorUtilities.CreateInstance<NetatmoSecurity>(serviceProvider), //memorystorage
               ActivatorUtilities.CreateInstance<Hue>(serviceProvider),
               ActivatorUtilities.CreateInstance<AzureRelay>(serviceProvider)
          ];
        }
    }
}