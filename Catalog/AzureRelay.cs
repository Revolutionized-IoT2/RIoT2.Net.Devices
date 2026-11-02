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
        // All are required, so binding sets them before ConfigureDevice.
        [DeviceParameter("relayNamespace", Required = true, Description = "<namespace>.servicebus.windows.net")]
        public string RelayNamespace { get; set; } = null!;

        [DeviceParameter("connectionName", Required = true, Description = "Hybrid connection name")]
        public string ConnectionName { get; set; } = null!;

        [DeviceParameter("keyName", Required = true, Description = "Shared access policy name")]
        public string KeyName { get; set; } = null!;

        [DeviceParameter("key", Required = true, Secret = true, Description = "Shared access key")]
        public string Key { get; set; } = null!;
    }

    [RequiresHardware("Azure Relay namespace")]
    public class AzureRelay : AsyncDeviceBase<AzureRelayOptions>, IDeviceWithConfiguration
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
            ClassFullName = GetType().FullName!,
            // The node adds the parameters from AzureRelayOptions.
            ReportTemplates = []
        };

        public override void ConfigureDevice()
        {
            _azureRelayService.Configure(Options.RelayNamespace, Options.ConnectionName, Options.KeyName, Options.Key);
        }

        protected override Task StartDeviceAsync(CancellationToken cancellationToken) => _azureRelayService.StartAsync();

        protected override Task StopDeviceAsync(CancellationToken cancellationToken) => _azureRelayService.StopAsync();

        protected override Task RefreshAsync(ReportTemplate? report, CancellationToken cancellationToken) => Task.CompletedTask;
    } 
}
