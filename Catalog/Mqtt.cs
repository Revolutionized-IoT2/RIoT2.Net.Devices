using Microsoft.Extensions.Logging;
using RIoT2.Core;
using RIoT2.Core.Abstracts;
using RIoT2.Core.Interfaces;
using RIoT2.Core.Models;
using RIoT2.Core.Plugins;
using RIoT2.Core.Utils;

namespace RIoT2.Net.Devices.Catalog
{
    /// <summary>The <see cref="Mqtt"/> device's parameters.</summary>
    public sealed class MqttOptions
    {
        [DeviceParameter("clientId", Description = "MQTT client id, unique on the broker, for example riot2-mqtt-device")]
        public string ClientId { get; set; }

        [DeviceParameter("serverUrl", Required = true, Description = "Broker host name or IP address (port 1883)")]
        public string ServerUrl { get; set; }

        [DeviceParameter("userName", Description = "Broker user; leave empty to connect anonymously")]
        public string UserName { get; set; }

        [DeviceParameter("password", Secret = true, Description = "Broker password")]
        public string Password { get; set; }

        [DeviceParameter("subscribeTopics", Description = "Topics reported as text, separated by ';', for example sensors/temperature;sensors/humidity")]
        public string SubscribeTopics { get; set; }
    }

    /// <summary>
    /// Mqtt Device is used to connect any mqtt device into system...
    /// </summary>
    [RequiresHardware("MQTT broker")]
    public class Mqtt : AsyncDeviceBase<MqttOptions>, IAsyncCommandDevice, IDeviceWithConfiguration
    {
        private MqttClient _mqttClient;

        public Mqtt(ILogger logger) : base(logger) { }

        public void ExecuteCommand(string commandId, string value) =>
            LegacySync.Wait(ExecuteCommandAsync(commandId, value, CancellationToken.None));

        public async Task ExecuteCommandAsync(string commandId, string value, CancellationToken cancellationToken)
        {
            Logger.LogInformation("Executed command: {commandId}", commandId);

            var command = CommandTemplates?.FirstOrDefault(x => x.Id == commandId);
            if (command == null)
                return;

            cancellationToken.ThrowIfCancellationRequested();
            await _mqttClient.PublishAsync(command.Address, value, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        public DeviceConfiguration GetConfigurationTemplate() => new()
        {
            Id = Guid.NewGuid().ToString(),
            Name = "MQTT",
            ClassFullName = GetType().FullName,
            // The node adds the parameters from MqttOptions.
            CommandTemplates = [],
            ReportTemplates = []
        };

        public override void ConfigureDevice() =>
            _mqttClient = new MqttClient(Options.ClientId, Options.ServerUrl, Options.UserName, Options.Password);

        protected override async Task StartDeviceAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _mqttClient.Start((Options.SubscribeTopics ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            _mqttClient.MessageReceived += mqttClient_MessageReceived;
        }

        protected override Task RefreshAsync(ReportTemplate report, CancellationToken cancellationToken) => Task.CompletedTask;

        protected override async Task StopDeviceAsync(CancellationToken cancellationToken)
        {
            if (_mqttClient == null)
                return;

            _mqttClient.MessageReceived -= mqttClient_MessageReceived;
            await _mqttClient.Stop();
        }

        private void mqttClient_MessageReceived(MqttEventArgs mqttEventArgs)
        {
            var report = ReportTemplates?.FirstOrDefault(x => x.Address.ToLower() == mqttEventArgs.Topic.ToLower());
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
    }
}
