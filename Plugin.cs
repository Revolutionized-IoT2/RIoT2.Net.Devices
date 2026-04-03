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
            services.AddSingleton<IAzureRelayService, AzureRelayService>();
            services.AddSingleton<IMemoryStorageService, MemoryStorageService>();
            services.AddSingleton<IApSystemsClientService, ApSystemsClientService>();
            services.AddSingleton<IEufySecurityService, EufySecurityService>();

            //Register devices as singletons
            services.AddSingleton<Core.Interfaces.IDevice, Web>();
            services.AddSingleton<Core.Interfaces.IDevice, Catalog.Timer>();
            services.AddSingleton<Core.Interfaces.IDevice, Virtual>();
            services.AddSingleton<Core.Interfaces.IDevice, Mqtt>();
            services.AddSingleton<Core.Interfaces.IDevice, WaterConsumption>();
            services.AddSingleton<Core.Interfaces.IDevice, Messaging>();
            services.AddSingleton<Core.Interfaces.IDevice, FTP>();
            services.AddSingleton<Core.Interfaces.IDevice, ElectricityPrice>();
            services.AddSingleton<Core.Interfaces.IDevice, EasyPLC>();
            services.AddSingleton<Core.Interfaces.IDevice, NetatmoWeather>();
            services.AddSingleton<Core.Interfaces.IDevice, NetatmoSecurity>();
            services.AddSingleton<Core.Interfaces.IDevice, Hue>();
            services.AddSingleton<Core.Interfaces.IDevice, AzureRelay>();
            services.AddSingleton<Core.Interfaces.IDevice, ApSystems>();
            services.AddSingleton<Core.Interfaces.IDevice, EufySecurity>();

            //AddControllers
            services.AddControllers();
        }
    }
}