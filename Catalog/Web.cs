using Microsoft.Extensions.Logging;
using RIoT2.Core;
using RIoT2.Core.Abstracts;
using RIoT2.Core.Interfaces;
using RIoT2.Core.Models;
using RIoT2.Net.Devices.Services.Interfaces;

namespace RIoT2.Net.Devices.Catalog
{
    public class Web : DeviceBase, ICommandDevice
    {
        private readonly IWebhookService _webhookService;

        public void ExecuteCommand(string commandId, string value)
        {
            Logger.LogInformation("Executed command: {commandId}", commandId);

            var command = CommandTemplates.FirstOrDefault(x => x.Id == commandId);
            if (command == null)
                return;

            //TODO do we need to map incoming value to command model?
            var result = _webhookService.SendMessageAsync(command.Address, value).Result;

            //check if there is report with command id address. If there is forward results there...
            var report = ReportTemplates.FirstOrDefault(x => x.Address == commandId);

            if (report == null || string.IsNullOrEmpty(result))
                return;

            SendReport(this, new Report()
            {
                Id = commandId,
                TimeStamp = DateTime.UtcNow.ToEpoch(),
                Value = new ValueModel(result)
            });
        }

        public Web(ILogger logger, IWebhookService webhookService) : base(logger)
        {
            _webhookService = webhookService;
        }

        private void webhookService_WebhookReceived(string address, string content)
        {
            var report = ReportTemplates.FirstOrDefault(x => x.Address.ToLower() == address.ToLower());

            if (report == null)
                return;

            SendReport(this, new Report()
            {
                Id = report.Id,
                TimeStamp = DateTime.UtcNow.ToEpoch(),
                Value = new ValueModel(content)
            });
        }

        public override void StartDevice()
        {
            _webhookService.WebhookReceived += webhookService_WebhookReceived;
        }

        public override void StopDevice()
        {
            _webhookService.WebhookReceived -= webhookService_WebhookReceived;
        }

        public override void ConfigureDevice()
        { }
    }
}
