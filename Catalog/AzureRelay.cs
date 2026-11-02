using Microsoft.Extensions.Logging;
using RIoT2.Core.Abstracts;
using RIoT2.Core.Interfaces;
using RIoT2.Core.Models;
using RIoT2.Core.Plugins;
using RIoT2.Net.Devices.Services.Interfaces;

namespace RIoT2.Net.Devices.Catalog
{
    /// <summary>The <see cref="AzureRelay"/> device's parameters: an Azure Relay hybrid connection.</summary>
    public sealed class AzureRelayOptions
    {
        [DeviceParameter("relayNamespace", Required = true, Description = "<namespace>.servicebus.windows.net")]
        public string RelayNamespace { get; set; }

        [DeviceParameter("connectionName", Required = true, Description = "Hybrid connection name")]
        public string ConnectionName { get; set; }

        [DeviceParameter("keyName", Required = true, Description = "Shared access policy name")]
        public string KeyName { get; set; }

        [DeviceParameter("key", Required = true, Secret = true, Description = "Shared access key")]
        public string Key { get; set; }
    }

    [RequiresHardware("Azure Relay namespace")]
    public class AzureRelay : DeviceBase<AzureRelayOptions>, IDeviceWithConfiguration
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
            // The node adds the parameters from AzureRelayOptions.
            ReportTemplates = []
        };

        public override void ConfigureDevice()
        {
            _azureRelayService.Configure(Options.RelayNamespace, Options.ConnectionName, Options.KeyName, Options.Key);
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
