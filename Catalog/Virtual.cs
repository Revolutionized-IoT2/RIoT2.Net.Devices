using Microsoft.Extensions.Logging;
using RIoT2.Core;
using RIoT2.Core.Abstracts;
using RIoT2.Core.Interfaces;
using RIoT2.Core.Models;

namespace RIoT2.Net.Devices.Catalog
{
    /// <summary>Keeps values in memory: a command stores its value and reports it on the matching report template.</summary>
    public class Virtual : AsyncDeviceBase, IAsyncCommandDevice
    {
        private Dictionary<string, object>? _virtualData;

        public Virtual(ILogger logger) : base(logger) { }

        public Task ExecuteCommandAsync(string commandId, string value, CancellationToken cancellationToken)
        {
            Logger.LogInformation("Executed command: {commandId}", commandId);

            var command = CommandTemplates?.FirstOrDefault(x => x.Id == commandId);
            if (command != null)
            {
                // The value arrives serialized as JSON; parse it, so the report has the commanded value and type.
                UpdateData(command.Address, new ValueModel(value, enforceCamelCase: false));
            }
            return Task.CompletedTask;
        }

        public void UpdateData(string address, object data) 
        {
            if (_virtualData == null)
                return;

            if (!_virtualData.ContainsKey(address))
                _virtualData.Add(address, data);
            else
                _virtualData[address] = data;

            var report = ReportTemplates?.FirstOrDefault(x => x.Address == address);
            if (report == null)
                return;

            SendReport(this, new Report() {
                Id = report.Id,
                TimeStamp = DateTime.UtcNow.ToEpoch(),
                Value = new ValueModel(data),
                Filter = ""
            });
        }

        protected override Task StartDeviceAsync(CancellationToken cancellationToken)
        {
            _virtualData = new Dictionary<string, object>();
            return Task.CompletedTask;
        }

        protected override Task StopDeviceAsync(CancellationToken cancellationToken)
        {
            _virtualData = null;
            return Task.CompletedTask;
        }

        public override void ConfigureDevice()
        { 
        }
    }
}
