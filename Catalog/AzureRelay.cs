using Microsoft.Extensions.Logging;
using RIoT2.Core.Abstracts;
using RIoT2.Core.Interfaces;
using RIoT2.Core.Models;
using RIoT2.Core.Plugins;
using RIoT2.Net.Devices.Services.Interfaces;

namespace RIoT2.Net.Devices.Catalog
{
    [RequiresHardware("Azure Relay namespace")]
    public class AzureRelay : DeviceBase, IDeviceWithConfiguration
    {
        private readonly ILogger _logger;
        private readonly IAzureRelayService _azureRelayService;

        public AzureRelay(ILogger logger, IAzureRelayService azureRelayService) : base(logger)
        {
            _logger = logger;
            _azureRelayService = azureRelayService;
            _azureRelayService.MessageReceived += _azureRelayService_MessageReceived;
        }

        ~AzureRelay() 
        {
            _azureRelayService.MessageReceived -= _azureRelayService_MessageReceived;
        }

        private void _azureRelayService_MessageReceived(string method, string body, Dictionary<string, string> querystrings, Dictionary<string, string> headers)
        {
            //TODO 
            //throw new NotImplementedException();
        }

        public DeviceConfiguration GetConfigurationTemplate() => new()
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Azure Relay",
            ClassFullName = GetType().FullName,
            DeviceParameters = new Dictionary<string, string>
            {
                { "relayNamespace", "<namespace>.servicebus.windows.net" },
                { "connectionName", "<hybrid-connection>" },
                { "keyName", "<shared-access-policy>" },
                { "key", "<shared-access-key>" }
            },
            ReportTemplates = []
        };

        public override void ConfigureDevice()
        {
            _azureRelayService.Configure(
                GetConfiguration<string>("relayNamespace"),
                GetConfiguration<string>("connectionName"),
                GetConfiguration<string>("keyName"),
                GetConfiguration<string>("key")
                );
        }

        public override void StartDevice()
        {
            _azureRelayService?.StartAsync();
        }

        public override void StopDevice()
        {
            _azureRelayService?.StopAsync();
        }
    } 
}
