using Microsoft.Extensions.Logging;
using RIoT2.Core;
using RIoT2.Core.Abstracts;
using RIoT2.Core.Interfaces;
using RIoT2.Core.Models;
using RIoT2.Net.Devices.Models;
using RIoT2.Net.Devices.Services.Interfaces;

namespace RIoT2.Net.Devices.Catalog
{
    public class EufySecurity : DeviceBase, IDeviceWithConfiguration, ICommandDevice
    {
        private readonly IEufySecurityService _eufySecurityService;
        public EufySecurity(ILogger logger, IEufySecurityService eufySecurityService) : base(logger)
        {
            _eufySecurityService = eufySecurityService;
        }

        public override void ConfigureDevice()
        {
            var ip = GetConfiguration<string>("serviceIp");
            var port = GetConfiguration<int>("port");
            _eufySecurityService.Configure(ip, port);
        }

        public DeviceConfiguration GetConfigurationTemplate()
        {
            var deviceConfiguration = new DeviceConfiguration();
            deviceConfiguration.Id = Guid.NewGuid().ToString();
            deviceConfiguration.Name = "Eufy Security";
            deviceConfiguration.DeviceParameters = new Dictionary<string, string>();
            deviceConfiguration.DeviceParameters.Add("serviceIp", "192.168.0.36");
            deviceConfiguration.DeviceParameters.Add("port", "3000");
            deviceConfiguration.ClassFullName = this.GetType().FullName;
           
            //Eufy must be connected, other we can't provide templates.
            if (_eufySecurityService.DeviceProperties == null || _eufySecurityService.StationProperties == null) 
                return deviceConfiguration;

            var reportConfigurations = new List<ReportTemplate>();
            var commandConfigurations = new List<CommandTemplate>();

            var filters = _eufySecurityService.DeviceProperties.Select(x =>  x.Value.Name).ToArray();

            reportConfigurations.Add(new ReportTemplate()
            {
                Id = Guid.NewGuid().ToString(),
                Address = "personDetected",
                Name = "Person Detected",
                Type = Core.ValueType.Boolean,
                Filters = filters
            });

            reportConfigurations.Add(new ReportTemplate()
            {
                Id = Guid.NewGuid().ToString(),
                Address = "motionDetected",
                Name = "Motion Detected",
                Type = Core.ValueType.Boolean,
                Filters = filters
            });

            reportConfigurations.Add(new ReportTemplate()
            {
                Id = Guid.NewGuid().ToString(),
                Address = "personName",
                Name = "Person Name",
                Type = Core.ValueType.Text,
                Filters = filters
            });

            reportConfigurations.Add(new ReportTemplate()
            {
                Id = Guid.NewGuid().ToString(),
                Address = "petDetected",
                Name = "Pet Detected",
                Type = Core.ValueType.Boolean,
                Filters = filters
            });

            reportConfigurations.Add(new ReportTemplate()
            {
                Id = Guid.NewGuid().ToString(),
                Address = "soundDetected",
                Name = "Sound Detected",
                Type = Core.ValueType.Boolean,
                Filters = filters
            });

            reportConfigurations.Add(new ReportTemplate()
            {
                Id = Guid.NewGuid().ToString(),
                Address = "strangerPersonDetected",
                Name ="Stranger Person Detected",
                Type = Core.ValueType.Boolean,
                Filters = filters
            });

            reportConfigurations.Add(new ReportTemplate()
            {
                Id = Guid.NewGuid().ToString(),
                Address = "vehicleDetected",
                Name = "Vehicle Detected",
                Type = Core.ValueType.Boolean,
                Filters = filters
            });

            //TODO Tsekkaan netatmo toteutus ja tee yhdenmukainen...
            reportConfigurations.Add(new ReportTemplate()
            {
                Id = Guid.NewGuid().ToString(),
                Address = "picture",
                Name = "Picture",
                Type = Core.ValueType.Entity,
                Model = new ValueModel(new SecurityReport()
                {
                    ImageUrl = "http://url-to-image",
                    Source = "Camera name"
                }),
                Filters = filters
            });

            reportConfigurations.Add(new ReportTemplate()
            {
                Id = Guid.NewGuid().ToString(),
                Address = _eufySecurityService.StationProperties.SerialNumber +"|guardMode",
                Name = "Guard Mode", 
                Type = Core.ValueType.Number,
                Model = 1
            });

            //Commands
            commandConfigurations.Add(new CommandTemplate()
            {
                Id = Guid.NewGuid().ToString(),
                Address = _eufySecurityService.StationProperties.SerialNumber + "|guardMode",
                Name = "Set Guard Mode",
                Type = Core.ValueType.Number,
                Model = 1
            });

            //Add device enable/ disable commands
            //Add more commands if needed -> refresh device pictures?

            deviceConfiguration.ReportTemplates = reportConfigurations;
            deviceConfiguration.CommandTemplates = commandConfigurations;
            return deviceConfiguration;
        }

        public void ExecuteCommand(string commandId, string value)
        {
            var command = CommandTemplates.FirstOrDefault(x => x.Id == commandId);
            if (command == null)
                return;

            //change to more generic, if more commads are added
            if (command.Address == _eufySecurityService.StationProperties.SerialNumber + "|guardMode")
            {
                int mode = 0;
                if(Int32.TryParse(value, out mode))
                    _eufySecurityService.SendCommand(EufyCommand.GetCommand_StationSetProperties(commandId, _eufySecurityService.StationProperties.SerialNumber, "guardMode", mode)).Wait();
            }
        }

        public override void StartDevice()
        {
            _eufySecurityService.Start();
            _eufySecurityService.EufyEvent += _eufySecurityService_EufyEvent;
            //wait a bit to get initial properties, otherwise we won't have templates to send reports with
            Task.Delay(2000).Wait();
            sendInitialReports();
        }

        private void sendInitialReports() 
        {
            foreach (var device in _eufySecurityService.DeviceProperties) //Only send pictures as initial reports.
            {
                sendReport("picture", device.Value.Picture, device.Value.Name);
            }

            sendReport(_eufySecurityService.StationProperties.SerialNumber + "|guardMode", _eufySecurityService.StationProperties.GuardMode, null);
        }

        private void _eufySecurityService_EufyEvent(EufyEventMessage data)
        {
            string camName = null;
            string address = data.Event.Name;
            if (data.Event.Source != "station")
                camName = _eufySecurityService.DeviceProperties.FirstOrDefault(x => x.Value.SerialNumber == data.Event.SerialNumber).Value.Name;

            if (camName == null) //if we don't have camera name, we are sending station level event
                address = _eufySecurityService.StationProperties.SerialNumber + "|" + address;

            sendReport(address, data.Event.Value, camName);
        }

        private void sendReport(string address, object value, string camName) 
        {
            var template = ReportTemplates.FirstOrDefault(x => x.Address == address);
            if (template == null) 
                return;

            SendReport(this, new Report()
            {
                Id = template.Id,
                Value = new ValueModel(value),
                TimeStamp = DateTime.UtcNow.ToEpoch(),
                Filter = camName
            });
        }

        public override void StopDevice()
        {
            _eufySecurityService.Stop();
        }
    }
}
