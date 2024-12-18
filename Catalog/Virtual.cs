using Microsoft.Extensions.Logging;
using RIoT2.Core;
using RIoT2.Core.Abstracts;
using RIoT2.Core.Interfaces;
using RIoT2.Core.Models;

namespace RIoT2.Net.Devices.Catalog
{
    internal class Virtual : DeviceBase, ICommandDevice
    {
        private Dictionary<string, object> _virtualData;

        internal Virtual(ILogger logger) : base(logger) { }

        public void ExecuteCommand(string commandId, string value)
        {
            Logger.LogInformation("Executed command: {commandId}", commandId);

            var command = CommandTemplates.FirstOrDefault(x => x.Id == commandId);
            if (command == null)
                return;

            UpdateData(command.Address, value);
        }

        public void UpdateData(string address, object data) 
        {
            if (_virtualData == null)
                return;

            if (!_virtualData.ContainsKey(address))
                _virtualData.Add(address, data);
            else
                _virtualData[address] = data;

            var report = ReportTemplates.FirstOrDefault(x => x.Address == address);
            if (report == null)
                return;

            SendReport(this, new Report() {
                Id = report.Id,
                TimeStamp = DateTime.UtcNow.ToEpoch(),
                Value = new ValueModel(data),
                Filter = ""
            });
        }

        public void Start()
        {
            _virtualData = new Dictionary<string, object>();
        }

        public void Stop()
        {
            _virtualData = null;
        }
    }
}
