using Microsoft.Extensions.Logging;
using RIoT2.Core;
using RIoT2.Core.Abstracts;
using RIoT2.Core.Interfaces;
using RIoT2.Core.Models;
using RIoT2.Core.Utils;

namespace RIoT2.Net.Devices.Catalog
{
    /// <summary>
    /// Mqtt Device is used to connect any mqtt device into system...
    /// </summary>
    internal class Mqtt : DeviceBase, ICommandDevice
    {
        private MqttClient _mqttClient;
        private string _topics;

        internal Mqtt(ILogger logger) : base(logger) { }

        public async void ExecuteCommand(string commandId, string value)
        {
            Logger.LogInformation("Executed command: {commandId}", commandId);

            var command = CommandTemplates.FirstOrDefault(x => x.Id == commandId);
            if (command == null)
                return;

            await _mqttClient.Publish(command.Address, value);
        }

        public override void ConfigureDevice()
        {
            string clientId = GetConfiguration<string>("clientId");
            string serverUrl = GetConfiguration<string>("serverUrl");
            string userName = GetConfiguration<string>("userName");
            string password = GetConfiguration<string>("password");
            _topics = GetConfiguration<string>("subscribeTopics");

            _mqttClient = new MqttClient(clientId, serverUrl, userName, password);
        }

        public async void Start()
        {
            await _mqttClient.Start(_topics.Split(';'));
            _mqttClient.MessageReceived += mqttClient_MessageReceived;
        }

        private void mqttClient_MessageReceived(MqttEventArgs mqttEventArgs)
        {
            var report = ReportTemplates.FirstOrDefault(x => x.Address.ToLower() == mqttEventArgs.Topic.ToLower());
            if (report == null)
                return;

            SendReport(this, new Report()
            {
                Id = report.Id,
                TimeStamp = DateTime.UtcNow.ToEpoch(),
                Value = new ValueModel(mqttEventArgs.Message),
                Filter = ""
            });
        }

        public async void Stop()
        {
            await _mqttClient.Stop();
        }
    }
}
