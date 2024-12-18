using Microsoft.Extensions.Logging;
using RIoT2.Core.Abstracts;
using RIoT2.Core.Interfaces;
using RIoT2.Core.Interfaces.Services;

namespace RIoT2.Net.Devices.Catalog
{
    internal class AzureRelay : DeviceBase, IDevice
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

        public override void ConfigureDevice()
        {
            _azureRelayService.Configure(
                GetConfiguration<string>("relayNamespace"),
                GetConfiguration<string>("connectionName"),
                GetConfiguration<string>("keyName"),
                GetConfiguration<string>("key")
                );
        }

        public void Start()
        {
            _azureRelayService?.StartAsync();
        }

        public void Stop()
        {
            _azureRelayService?.StopAsync();
        }
    } 
}
